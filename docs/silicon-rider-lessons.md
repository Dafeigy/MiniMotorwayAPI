# Lessons from Silicon Rider Bench for the Agent API

**English** | [简体中文](silicon-rider-lessons.zh-CN.md)

Reference project: [KCORES/silicon-rider-bench](https://github.com/KCORES/silicon-rider-bench).
The conclusions below are based on its `src/tools`, `src/core/simulator.ts`, and
`src/client` implementations. That project owns a discrete simulator; this
project connects to Mini Motorways' continuously running native simulation.

## Contract patterns worth adopting

1. **Separate queries from actions.** Silicon Rider divides state, order, map,
   and distance queries from accept, move, pickup, and wait mutations. It uses
   shared parameter definitions and structured success/error responses. This
   project likewise keeps `GET` reads separate from `POST` operations. A client
   tool layer can expose model-friendly actions, but native execution remains
   authoritative.
2. **Keep a compact status and fetch detailed observations on demand.** The
   reference refreshes a compact status near the end of every model prompt and
   obtains other data through query tools. `/game/status` and `/events` should
   be lightweight polling paths, while full `/snapshot` capture belongs at a
   decision boundary or planning request. At the time of this comparison,
   `/game/status` still built a full snapshot and was not a cheap poll.
3. **Organize observation and decisions around simulation boundaries.** The
   reference action returns a time cost that advances its simulator. This
   project cannot choose native step duration, so it must establish events and
   pause boundaries after native updates, then capture one immutable
   observation. `snapshotId` is only an API capture sequence; decisions and
   actions need independent `episodeId` and native-time identity. Pause while
   the Agent reasons, record no-action decisions explicitly, then resume.
4. **Separate an action response from its final effect.** A self-owned simulator
   can mutate state inside a tool handler. Here, `202 Scheduled` means only that
   the native command was queued. Track `queued → scheduled →
   executed/rejected/expired` by `actionId`, record actual native execution
   time, and confirm visible state at the same or a later boundary.
   `GET /requests` covers HTTP/main-thread processing, not the final action.
5. **Execute multiple actions in order and state their preconditions.** The
   reference runs tool calls sequentially even when a model emits several at
   once. Submit roads, facilities, and weekly upgrades one at a time and confirm
   each result so they do not share stale resource/map assumptions. A cheap
   legality query is useful, but native rules must validate again at execution.

## Patterns that do not transfer directly

- The reference `wait` handler advances the clock, and its simulator also
  advances by the returned `timeCost`. This project should have one explicit
  owner of simulation time and must not equate HTTP calls/model turns with
  native game time.
- Parts of the reference's order generation use conversation turns and
  `Math.random()`, while path-efficiency scoring still contains fixed values.
  Benchmarks here should record game version, map, mode, seed, events, actions,
  and native time, then calculate metrics from real execution.
- Browser live visualization is an observer UI, not an authoritative Agent
  state source. Authority remains the main-thread observation and native action
  execution record.

## Recommended implementation order

Complete native single-action edit paths and weekly upgrade selection first,
then add final action results, lightweight events, and native-time boundaries.
Each action must distinguish malformed input, native rejection, no change,
scheduled success, and final execution. Automated evaluation must never count
scheduled success as an applied change.
