# P0 Agent and Reinforcement-Learning Control Plan

**English** | [简体中文](p0-agent-rl-plan.zh-CN.md)

This document records the agreed P0 direction and serves as the implementation
and acceptance checklist. Existing `/snapshot`, road-edit, restart, and
pause/resume behavior remains compatible. P0 does not add a client-controlled
`/game/step` duration or batch edits; roads are submitted one edit at a time.

For lessons from an external benchmark and the native simulation boundaries
that this project must preserve, see [Lessons from Silicon Rider Bench for the
Agent API](silicon-rider-lessons.md).

## Decision boundaries

The same events, snapshots, and action records support two control styles:

| Consumer | Observation/decision point | Game behavior |
| --- | --- | --- |
| Agent | Poll lightweight event metadata, decide whether to pause; selected critical events can auto-pause | Pause while reasoning/editing, then resume |
| RL model | Minimum native simulation interval since the latest executed action, or an earlier critical event | Auto-pause at the boundary; remain paused during inference and action submission |

Intervals use native simulation time/ticks, not HTTP arrival time or wall time.
A boundary is selected after a simulation update; events and current state are
resolved before one snapshot is captured. Multiple reasons in the same
boundary are merged into a `reasons` array. A no-action decision resumes after
an explicit no-op.

Normal events can be polled and paused by the Agent. Because event occurrence
and pause can be separated by several simulation ticks, the event stores its
occurrence tick while the snapshot stores the actual capture tick. Events that
need immediate handling can auto-pause. Required events include new houses,
new destinations, weekly upgrades, overcrowding warning/start, and terminal
state. A destination and its carpark create one decision event. Exact hook
timing remains game-version dependent and must be verified at runtime.

## Protocol and data contract

The following is the target contract; only delivered fields/endpoints are
current API behavior.

1. `GET /events?after=<eventId>` reads a bounded lightweight event log without
   scanning tiles/vehicles or building a full snapshot. It returns the latest
   ID, event list, and an explicit gap flag. Clients deduplicate by `eventId`;
   after overflow they refresh the full snapshot.
2. `/snapshot` includes `episodeId`, native simulation position, the observed
   event boundary, and terminal reason. `snapshotId` remains a capture sequence,
   not a simulation tick. Reads at one decision boundary reuse one immutable
   snapshot.
3. State-changing requests carry a client-generated `requestId`, preferably in
   an HTTP header for bodyless controls. The same ID and content execute at
   most once within a run; conflicting content is rejected. Legacy clients can
   omit the ID but cannot safely retry a lost response.
4. Actions have an `actionId` and explicit `queued`, `scheduled`, `executed`,
   `rejected`, or `expired` state. HTTP `202` only means accepted/scheduled.
   Final results include native execution time. A queued request that expires
   must not execute later.
5. RL intervals begin at the latest action's **actual execution** time. A
   critical event can interrupt the interval. Terminal state always produces
   an observable event and final snapshot.

Paused road edits first return scheduled. Live tests showed native edit
commands completing while `timeScaleMode=Paused` and the game clock remained
fixed, so clients must not assume resume is required. Submit edits individually
and confirm their later snapshot/action state.

## Implementation order and acceptance

### 0. Compile baseline

- Fix the C# syntax in the pending Logo change while preserving its purpose.
- Write Release builds to a repository staging directory, never directly over
  the installed plugin.

### 1. Request lifecycle and idempotency

- Add enqueue time, deadline, and atomic state transitions. Cancel requests
  that expire before starting.
- Add `requestId`, conflict detection, and stored replay results.
- Accept when: a request queued through a main-thread stall never executes
  after expiry; duplicate requests execute once; conflicting reuse is rejected.

### 2. Episode, simulation time, and final action result

- Create `episodeId` at native game initialization and record terminal cause.
- Associate scheduled simulation commands with `actionId` and record their
  actual execution time without changing existing immediate responses.
- Accept when: old actions cannot leak across restart; road changes reach a
  final result; snapshot/action time ordering is explainable.

### 3. Lightweight events and two decision policies

- Generate a bounded log from native hooks or low-cost checks. `/events` must
  not capture a full snapshot.
- Let Agents poll and optionally auto-pause critical events; let RL pause at a
  native-time interval. Capture only one snapshot per boundary.
- Accept when: no-event polling does not scan the map; same-tick reasons merge;
  critical events survive pause; inference time does not advance simulation.

### 4. Individual actions and decision observations

- Add weekly upgrade, traffic light, roundabout, motorway, and explicit no-op
  actions, each submitted and confirmed individually.
- Add map bounds, terrain, motorway endpoints, traffic load, and legality
  information. Start with one-candidate legality before evaluating a full mask.
- Accept when: success, invalid position, insufficient resource, and duplicate
  cases are covered; observation fields match native state and the UI.

## Snapshot performance validation

The current implementation captures each read on Unity's main thread, scans
vehicles/tiles, and serializes JSON there. `/game/status` also builds a full
snapshot. Measure capture, serialization, payload, end-to-end latency, and
frame impact at different map sizes before assuming performance limits.

`X-Snapshot-Capture-Ms`, `X-Snapshot-Serialize-Ms`, and the read-only
`python tests/apitest.py --benchmark-snapshot 20` command are implemented. The
headers exclude queueing and transport; the script also measures client
latency. For a 19-tile new game, 20 samples produced medians of 3.293 ms
capture, 0.239 ms serialization, and 7.023 ms total, with 27.815 ms total p95.

For a later 222-tile, 72-vehicle, 10-destination, 3-motorway game, medians were
4.775 ms capture, 2.998 ms serialization, and 13.800 ms total. P95 values were
5.385 ms, 3.812 ms, and 36.847 ms; payload size was about 125,395 bytes. This
supports low-frequency control but not extreme maps or high-throughput RL.

Event and action-status polling should remain lightweight. P0 caches the full
snapshot only for one paused decision boundary. Moving serialization off the
main thread, versioned protocols, and general immutable observation pipelines
remain P1 unless measurements require earlier work.

## Current progress

- [x] Compile baseline: Logo fixed, repository-local Release build and startup verified.
- [ ] Request lifecycle and HTTP idempotency: IDs, queued timeout cancellation,
  replay, and `GET /requests` are implemented. Same-ID replay, conflicts, and
  duplicate roads passed live tests. Main-thread-stall expiry still needs stress
  testing. This endpoint reports HTTP/main-thread completion, not final native
  command execution.
- [ ] Episode, native time, and final action result: `episodeId`,
  `simulationTick`/`simulationTime`, terminal cause, `actionId`, and
  `GET /actions` are implemented. Road add/clear reached `executed`, each with
  one completed native command and execution time. UI restart created a new
  episode while preserving the seed; prior actions remained historical. A real
  game over recorded `overcrowding`, its destination, and a matching terminal
  event. Motorways aggregate two commands. API start/restart records
  `resultingEpisodeId`; queued old-episode actions expire. API restart identity
  and queued cross-episode expiry still need live coverage.
- [x] Lightweight events and decision policies: house/destination, weekly
  upgrade, overcrowding start/warning, and terminal events; RL interval pause;
  Agent `houseAdded` auto-pause; and immutable snapshot reuse all passed live
  validation. Decisions publish only after the game reports `Paused`. Event IDs
  increase across episodes, and each event carries its episode ID.
- [ ] Actions and observations: weekly upgrade, traffic light, roundabout,
  motorway, and no-op passed disposable-game tests. The corrected motorway
  schedules two native edits and consumes one upgrade; the insufficient-resource
  case returns `422 NotEnoughUpgrades`. Full/current bounds, terrain, road-chunk
  traffic, motorway endpoints, and single-candidate `/actions/legal` are
  implemented and runtime checked. Full-mask cost and systematic visual
  comparison remain open.
- [ ] Runtime performance and behavior: early and 222-tile benchmarks, road
  add/clear receipts, UI/API restart, pause/resume, a delivery, facilities,
  motorway, and real terminal state were exercised. Main-thread timeout races
  and API restart result identity remain. Immediate pause/resume
  `changed/currentMode` can be stale; confirm via a later snapshot.
