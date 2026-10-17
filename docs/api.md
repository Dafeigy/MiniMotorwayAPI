# Local HTTP API

**English** | [简体中文](api.zh-CN.md)

This document describes the currently implemented API. `Moretoways.cs` is the
source of truth. Planned endpoints such as `/v1/*` are not implemented and are
outside this reference.

## General conventions

- Base URL: `http://localhost:9000`. The listener is local-only. Responses are
  UTF-8 JSON with `Cache-Control: no-store`.
- Read endpoints use `GET`; operations use `POST`. Every path accepts CORS
  `OPTIONS` preflight. CORS allows any origin to use `GET`, `POST`, and
  `OPTIONS`, with `Content-Type` and `X-Request-Id` request headers.
- Requests are processed on Unity's main thread. A queued request expires with
  HTTP `503` if the main thread does not begin it within five seconds. An
  operation that already started may first return HTTP `202` with a `state`;
  query `/requests` for its final HTTP result.
- Request bodies for `/actions/road`, `/actions/clear`,
  `/actions/traffic-light`, `/actions/roundabout`, `/actions/motorway`,
  `/actions/upgrade`, `/decision/config`, `/game/new`, and `/game/restart` are
  limited to 4096 characters/declared bytes. JSON requests use
  `Content-Type: application/json`. `/actions/noop`, `/game/restart`,
  `/game/pause`, and `/game/resume` also accept bodyless POST requests,
  including requests without an explicit `Content-Length` header.
- Paths are case-sensitive. Unknown paths return `404`, method mismatches return
  `405`, and unhandled server failures return `500`.
- Operation responses include `X-Request-Id`. A client-supplied ID must contain
  1–128 ASCII letters, digits, `-`, `_`, `.`, or `:`. Reusing an ID with the
  exact same path and raw body replays the first result. Reusing it with a
  different path or body returns `409`. When omitted, the server generates an
  ID. The raw body is compared, so changing JSON whitespace counts as a
  different request.
- Every operation response contains `requestId`, `actionId`, and the
  submission-time `episodeId`. `requestId` tracks HTTP/main-thread processing;
  `actionId` tracks the game action. They are distinct identifiers.
- HTTP `202` means accepted or scheduled; it **does not guarantee simulation
  execution**. Query `/actions?id=<actionId>` for the final state and use a
  later `/snapshot` to verify visible roads, facilities, motorways, and
  resources. Verify weekly upgrades through `/game/upgrades`, and poll
  `/game/status` after starting or restarting a game.

Successful `/snapshot`, `/tiles`, `/tiles/count`, `/destinations`, and
`/game/status` responses also include `X-Snapshot-Capture-Ms` and
`X-Snapshot-Serialize-Ms`. These are main-thread capture and serialization
times in milliseconds; they exclude queueing and network time.

## Endpoint overview

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/snapshot`, `/` | Full game snapshot |
| GET | `/tiles` | All tiles from the current snapshot |
| GET | `/tiles/count` | Tile count from the current snapshot |
| GET | `/destinations` | One record per destination building |
| GET | `/game/status` | Game status and new-game load progress |
| GET | `/game/maps` | Map identifiers and names available for a new game |
| GET | `/game/upgrades` | Pending weekly upgrade choices and UI readiness |
| GET | `/requests?id=<requestId>` | Operation request state and original HTTP result |
| GET | `/actions?id=<actionId>` | Scheduled/final game action state |
| GET | `/events?after=<eventId>` | Bounded lightweight game-event log |
| GET | `/decision/status` | Agent/RL policy and current decision boundary |
| POST | `/actions/legal` | Check one candidate without scheduling it |
| POST | `/actions/road` | Add one road connection |
| POST | `/actions/clear` | Clear roads or a selected facility type |
| POST | `/actions/traffic-light` | Add a traffic light |
| POST | `/actions/roundabout` | Add a roundabout |
| POST | `/actions/motorway` | Add a motorway between two endpoints |
| POST | `/actions/upgrade` | Select a weekly upgrade option |
| POST | `/actions/noop` | Record an explicit no-edit decision |
| POST | `/decision/config` | Configure Agent/RL automatic pauses |
| POST | `/game/new` | Start a normal-mode game |
| POST | `/game/restart` | Restart the current map |
| POST | `/game/pause` | Pause simulation |
| POST | `/game/resume` | Resume at normal speed |

## Read endpoints

### `GET /snapshot` (`GET /` is an alias)

Returns a full snapshot captured separately for each request. `success` is
`true`, and `timestamp` is UTC. `snapshotId` resets when a new game starts and
increments for each capture; it is not a frame number shared across requests.
`episodeId` is created at the native city-initialization boundary and remains
stable for the run. `simulationTick` is the native clock frame count and
`simulationTime` is game time from the same clock. HTTP reads do not advance
them while paused.

| Field | Meaning |
| --- | --- |
| `snapshotId`, `episodeId`, `timestamp` | Capture sequence, run ID, and capture time |
| `simulationTick`, `simulationTime`, `editCommandsExecuted` | Native frame, simulation time, and cumulative executed edit count |
| `terminalReason`, `terminalDestinationBuildingId` | Terminal cause and destination; `null` before game over. Normal failure is `overcrowding`; an end without a destination is `gameEnded` |
| `phase` | `menu`, `playing`, or `gameOver` |
| `gameUI`, `tilemapView`, `camera` | Whether the corresponding game objects are ready |
| `timeScaleMode` | Game time-scale mode, or `null` without a game UI |
| `initialSeed`, `map` | Seed actually used and map `cityName`; may be `null` before initialization |
| `mapBounds` | Full `full` bounds and expanding `current` bounds, each with `minX/minY/maxX/maxY` |
| `score`, `clock`, `resources` | Score, clock, and available upgrades; may be `null` when unavailable |
| `tileCount`, `tiles`, `vehicleCount`, `vehicles`, `motorwayCount`, `motorways` | Counts and corresponding arrays |

`clock` contains `time`, `hour`, `day`, and `week`. `resources` maps the current
game version's `UpgradeType` names to available quantities. `tileCount` can be
zero before a tilemap is ready; clients should wait for `phase: "playing"` and
a nonzero count.

Each `tiles[]` item contains:

| Field | Meaning |
| --- | --- |
| `coordinates` | `{ "x": integer, "y": integer }` |
| `contentType` | Native tile content name, such as `None`, `House`, or `Destination` |
| `buildingId`, `contentData` | Building ID and type-specific data; may be `null` |
| `roads` | Direction-to-road-state object containing only non-`None` roads |
| `hasTrafficLight`, `isRoundabout` | Traffic-light and roundabout-center flags |
| `drivewayDirection`, `unbuiltMotorwayId` | Driveway direction and unbuilt motorway ID |
| `terrain` | `buildable`, `zoneable`, `driveable`, `water`, `mountain`, `currentlyPlayable` |
| `traffic` | For road chunks: `laneCount`, `vehicleCount`, `stoppedVehicleCount`, `averageSpeed`; otherwise `null` |

House IDs use `H:x:y`. House `contentData` contains `groupIndex`,
`hasWaitingVehicle`, and `vehicleCounts` (`owned`, `waiting`, `available`, and
`realigning`). Destination IDs use `D:minX:minY`; every tile in a multi-tile
destination shares the same ID. Destination `contentData` contains:

- `groupIndex`, `totalDemand`, `isOvercrowding`, and `isUpgraded`.
- `demand`: `total`, `unassigned`, `assigned`, `unassignedByGroup`,
  `assignedByGroup`, `maximumBeforeOvercrowding`, `isOvercrowding`,
  `overcrowdingTime`, `overcrowdingSpeed`, `generationTimer`, and
  `totalServiced`. Group keys are JSON strings. Timer fields may be `null`
  while game objects are unavailable. `totalDemand` equals `demand.total`,
  which equals `unassigned + assigned`.
- `footprint`: occupied coordinates. Optional `carpark` contains `topLeft`,
  `drivewayTopLeft`, and `drivewayBottomRight`; unavailable entrances are
  `null`.

Each `vehicles[]` item contains `vehicleId`, `state`, `houseBuildingId`,
`destinationBuildingId`, and `lastVisitedDestinationBuildingId`. Building
references may be `null`. Destinations generate demand; demand is not bound to
a house until a vehicle is dispatched. `groupIndex` matches houses to
destinations.

Each `motorways[]` item contains `motorwayId`, `number`, `state`,
`hasConsumedUpgrade`, `startCoordinates`, `startDirection`, `endCoordinates`,
and `endDirection`. `hasConsumedUpgrade` comes from the simulation model. The
array is sorted by ID and excludes client edits not yet executed.

### `GET /tiles`, `GET /tiles/count`, `GET /destinations`

- `/tiles`: `{ success, snapshotId, episodeId, simulationTick, simulationTime, count, tiles, timestamp }`.
- `/tiles/count`: `{ success, snapshotId, episodeId, simulationTick, simulationTime, count, timestamp }`.
- `/destinations`: `{ success, snapshotId, episodeId, simulationTick, simulationTime, count, destinations, timestamp }`; each `buildingId` appears once.

### `GET /game/status`

Returns `{ success, snapshotId, episodeId, simulationTick, simulationTime,
terminalReason, terminalDestinationBuildingId, editCommandsExecuted, phase,
gameUI, tilemapView, camera, timeScaleMode, initialSeed, map, tileCount, score,
clock, resources, newGame, timestamp }`. `newGame` is `null` or
`{ map, seed, state, error }`; `state` is `loading`, `starting`, `initializing`,
`ready`, or `failed`. A load fails with an `error` after 30 seconds.

### `GET /game/maps`

Returns `{ "success": true, "maps": [{ "map": "LosAngeles", "name": "..." }] }`.
`map` is a case-sensitive `cityName`; use the running game's values. Returns
`503` when map services are unavailable.

### `GET /game/upgrades`

Returns `{ success, pending, pendingCount, ready, options }` without scanning
tiles or building a full snapshot. `options` describes the first pending group;
each item is `{ optionIndex, type, amount, additionalConcrete, selectable }`.
`optionIndex` is passed to `/actions/upgrade`. `ready` also requires the upgrade
screen to be present and through its transitions.

### `GET /requests?id=<requestId>`

Returns `{ success, requestId, actionId, actionState, state,
responseStatusCode, response }`. `state` is `queued`, `running`, `complete`, or
`expired`. The last two fields are `null` until completion, then contain the
original status and JSON response. Missing/invalid IDs return `400`; unknown
IDs return `404`.

### `GET /actions?id=<actionId>`

Returns `{ success, actionId, requestId, episodeId, resultingEpisodeId, path,
state, resultCode, error, responseStatusCode, queuedAt, scheduledAt, executedAt,
scheduledTick, scheduledSimulationTime, executedTick, executedSimulationTime,
scheduledCommands, executedCommands, failedCommands }`.

- `queued`: HTTP request has not started on the game thread.
- `scheduled`: at least one edit is scheduled, or a start/restart awaits city initialization.
- `executed`: every related `EditTileCommand` returned, or a synchronous action handler accepted the operation. Starts/restarts set `resultingEpisodeId`.
- `rejected`: parameters, rules, resources, or UI state rejected the action.
- `expired`: it timed out before starting or its episode ended first.

Road, clear, traffic-light, roundabout, and motorway actions count scheduled
and executed commands; a successful motorway uses two. The server retains the
most recent 4096 request/action records.

### `GET /events?after=<eventId>`

This lightweight path does not scan tiles/vehicles, build a snapshot, or emit
snapshot timing headers. `after` defaults to zero. It returns `{ success,
episodeId, after, oldestEventId, latestEventId, gap, events }`. The top-level
`episodeId` is current; each event is `{ eventId, episodeId, simulationTick,
simulationTime, timestamp, reasons, details }`. Event IDs increase across
restarts for the lifetime of the plugin process, so the bounded buffer can
contain prior episodes. Separate runs by each event's `episodeId`.

Reasons are `houseAdded`, `destinationAdded`, `upgradeAvailable`,
`overcrowdingWarning`, `overcrowdingStarted`, `terminal`, and `interval`.
Reasons in the same native tick and episode are merged. The server retains the
most recent 1024 boundaries. `gap: true` means the requested cursor predates
the retained range; read a full snapshot and continue from `latestEventId`.

### `GET /decision/status`

Returns `{ success, episodeId, mode, minimumSimulationInterval,
autoPauseReasons, pending, eventId, simulationTick, simulationTime, reasons,
snapshotId }`. `mode` is `off`, `agent`, or `rl`. A triggered boundary pauses
after the simulation update. Repeated `/snapshot` reads reuse one immutable
snapshot until an action invalidates it or the game resumes.

### `POST /actions/legal`

Read-only validation for one candidate. It creates no request/action ID,
schedules no command, and consumes no resources. Returns `{ success, type,
legal, resultCode, errorPosition }`; an illegal candidate still returns HTTP
`200` with `legal: false`. Malformed input returns `400`; missing active-game
services return `409`.

`type` may be `road`, `clear`, `traffic-light`, `roundabout`, `motorway`,
`upgrade`, or `noop`. Other fields match the corresponding execution endpoint;
`clear` uses `clearType`. Native `TileEditor` rules are reused, and temporary
edits are immediately released. Execution must validate again.

## Operation endpoints

The following status codes describe request handling. Confirm scheduled effects
through read endpoints.

### `POST /actions/road`

Body: `{"x":5,"y":7,"direction":"East"}`. Directions are `North`,
`NorthEast`, `East`, `SouthEast`, `South`, `SouthWest`, `West`, and
`NorthWest`, parsed case-insensitively.

### `POST /actions/clear`

Body: `{"x":5,"y":7}`. Roads are cleared by default. Optional `type` is
`Roads`, `TrafficLight`, `Roundabout`, `BuiltMotorways`, `UnbuiltMotorway`,
`Passages`, or `Any`, subject to native permanence rules.

Both road endpoints return `400` for malformed input, `409` without an active
game or when scheduling is refused, `503` when edit services are unavailable,
and `422` with `resultCode`/`errorPosition` when game rules reject the edit.
No-change success is HTTP `200`; scheduled success is HTTP `202`.

### `POST /actions/traffic-light`, `POST /actions/roundabout`

Body: `{"x":5,"y":7}`. They call the native `TileEditor` methods and use the
same scheduling/result format as roads. After HTTP `202`, verify
`hasTrafficLight`/`isRoundabout` and resources in a later snapshot.

### `POST /actions/motorway`

Body: `{"startX":1,"startY":2,"startDirection":"East","endX":8,
"endY":2,"endDirection":"West"}`. Endpoints must differ. The operation
validates the completed motorway, then schedules the native unbuilt endpoint
and completed motorway edits in order. HTTP `202` means both were scheduled;
verify `/snapshot.motorways`, endpoints, and resources later.

### `POST /actions/upgrade`

Body: `{"optionIndex":0}` using an index from `/game/upgrades.options`. The UI
must be ready. Invalid indices return `400`, unavailable/not-ready choices
return `409`, and unmapped buttons return `422`. HTTP `202` still requires a
later check of pending choices and resources.

### `POST /actions/noop`

No body. With an active game or upgrade screen, returns HTTP `200` with
`{ "success": true, "scheduled": false, "resultCode": "NoOp" }`; otherwise
returns `409`. It does not edit, select, pause/resume, or advance time.

### `POST /decision/config`

Body may contain `{ "mode":"rl", "minimumSimulationInterval":30.0,
"autoPauseReasons":["upgradeAvailable","overcrowdingWarning","terminal"] }`.
Omitted fields retain their values. The interval must be positive and uses
native simulation time. Auto-pause reasons accept event reasons other than the
RL-generated `interval`.

- `off`: no automatic decision boundaries; events continue.
- `agent`: only configured critical reasons pause automatically.
- `rl`: the interval pauses automatically; configured reasons can interrupt it.

The interval starts at the episode beginning or the latest actual execution of
an `/actions/*` action. Submit an action or `/actions/noop`, then resume. If the
game resumes without an action, an already-due RL interval can trigger again.

### `POST /game/new`

Body: `{"map":"LosAngeles","seed":12345,"startPaused":true}`. `map` is
required and comes from `/game/maps`. `seed` is an optional unsigned 32-bit
integer; it defaults to `20261014`. `startPaused` defaults to `false`. This
starts a normal-mode game and discards the current run.

Accepted loads return HTTP `202` with `resultCode: "NewGameLoading"`. Poll
`/game/status` until `newGame.state` is `ready`, `phase` is `playing`, map/seed
match, and `tileCount > 0`. Invalid input returns `400`, unknown maps `404`,
unsupported/currently busy UI `409`, and unavailable load services `503`.

### `POST /game/restart`

The body may be omitted or be `{"seed":12345}`. Omission reuses the current
`initialSeed`. Active and game-over states are supported. Accepted restarts
return HTTP `202` with `resultCode: "RestartRequested"`; poll `/game/status`
until the new playing episode has the expected seed and a nonzero tile count.

### `POST /game/pause`, `POST /game/resume`

No body. With an active game, returns `{ success, changed, previousMode,
currentMode }`; otherwise `409`. Pause stops simulation without opening the
pause menu. Resume always selects normal speed. The immediate mode can still
show the previous value, so confirm with `/game/status` or `/snapshot`.

## curl examples

```bash
curl http://localhost:9000/game/maps
curl http://localhost:9000/game/status

curl -X POST http://localhost:9000/actions/road \
  -H "Content-Type: application/json" \
  -H "X-Request-Id: road-5-7-east-1" \
  -d '{"x":5,"y":7,"direction":"East"}'

curl 'http://localhost:9000/requests?id=road-5-7-east-1'
curl 'http://localhost:9000/actions?id=<actionId>'
curl 'http://localhost:9000/events?after=0'
```

The default runtime validation is read-only: `python tests/apitest.py`.
State-changing road, pause, and restart checks require explicit options. See
the [snapshot roadmap](snapshot-roadmap.md) for planned snapshot semantics.
