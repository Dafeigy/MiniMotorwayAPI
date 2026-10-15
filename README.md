# Moretoways

BepInEx plugin for observing and controlling a running Mini Motorways game
through a local HTTP API. Road edits, road clearing, active-game restart, and
pause/resume are implemented; stepping and other upgrades are still under development.

## Build

The project currently references the local Mono game and BepInEx assemblies in
`Moretoways.csproj`. Update those paths if the game is installed elsewhere.
Override `OutputPath` to build without writing directly into the game directory:

```powershell
dotnet msbuild Moretoways.csproj /t:Build /p:Configuration=Debug /p:OutputPath=bin\Debug\
```

Copy the built `Moretoways.dll` and `Newtonsoft.Json.dll` into the game's
`BepInEx/plugins` directory if Newtonsoft.Json is not already available there.

## Grid visibility shortcut

Press `Ctrl+M` during a game to toggle always-visible world grid mode. Either
the left or right Ctrl key works. When enabled, the grid remains visible after
the mouse button is released, and each visible tile is labelled with its
`(x,y)` coordinate in semi-transparent text. The labels follow the tiles while
the camera moves or zooms. Press `Ctrl+M` again to hide the labels, disable the
mode immediately, and restore the game's original press-to-show,
release-to-hide behavior.

![Ctrl+M Grid Display with coordinates](imgs/ctrl+M-coordinates.png)

The toggle is not saved to a configuration file. It defaults to disabled each
time Mini Motorways starts.

## Read API

The listener accepts requests on `localhost:9000`.
Each request is passed to Unity's main thread; the server
thread never reads live Unity or game objects.
Browser clients may use cross-origin `GET` and `POST` requests; the listener
answers `OPTIONS` preflight requests and exposes the corresponding CORS headers.

| Endpoint | Response |
| --- | --- |
| `GET /snapshot` | Full game state snapshot. `/` is an alias. |
| `GET /tiles` | Tiles from a single snapshot. |
| `GET /tiles/count` | Tile count. |
| `GET /destinations` | One entry per destination building, including `groupIndex`, demand state, footprint, and carpark coordinates. |
| `GET /game/status` | Phase, score, clock, available resources, and readiness flags. |
| `POST /actions/road` | Add one road connection from `(x, y)` in `direction`. |
| `POST /actions/clear` | Clear roads on tile `(x, y)`, respecting the game's permanence rules. |
| `POST /game/restart` | Restart the current map from an active or game-over game. |
| `POST /game/pause` | Pause simulation while leaving the game board available for observation. |
| `POST /game/resume` | Resume simulation at normal speed. |

`/snapshot` includes a `snapshotId` that starts at 1 for each new game and
increases monotonically until the next game starts, plus a UTC timestamp,
phase (`menu`, `playing`, or `gameOver`), time scale mode, score, clock, available upgrades, and
`editCommandsExecuted` (a diagnostic count of simulation edit commands), and
tile and vehicle lists. Each tile contains its coordinates, content type and type-specific
data, directional road states, and traffic light / roundabout information.
Destination buildings occupy multiple tiles; tiles sharing a `buildingId`
represent one building. `/destinations` returns each building once.
`groupIndex` is the stable identifier to use for matching houses and
destinations; its display color may vary by city.

Each destination also includes a nested `demand` object. `unassigned` counts
pins that have not selected a vehicle yet, while `assigned` counts pins with a
vehicle dispatched but not yet arrived. The corresponding `*ByGroup` objects
show the vehicle group required by those pins. The object also reports the
overcrowding threshold and current timer state, the demand-generation timer,
and the number of pins serviced by the destination. The legacy `totalDemand`
field remains available and equals `demand.total`.

The top-level `vehicles` list reports each vehicle's stable `vehicleId`, current
behavior state, owning `houseBuildingId`, assigned `destinationBuildingId`, and
last visited destination. A non-null `destinationBuildingId` identifies an
active outbound delivery. House `contentData.vehicleCounts` reports owned,
waiting, currently available, and driveway-realigning vehicles. These fields
describe existing simulation relationships and do not run pathfinding.

The relevant portion of a snapshot has the following shape (unrelated fields
are omitted):

```json
{
  "initialSeed": 20261014,
  "vehicleCount": 1,
  "vehicles": [
    {
      "vehicleId": 21,
      "state": "DrivingToDestination",
      "houseBuildingId": "H:-5:-1",
      "destinationBuildingId": "D:-12:-3",
      "lastVisitedDestinationBuildingId": null
    }
  ],
  "tiles": [
    {
      "contentType": "House",
      "buildingId": "H:-5:-1",
      "contentData": {
        "groupIndex": 0,
        "hasWaitingVehicle": true,
        "vehicleCounts": {
          "owned": 2,
          "waiting": 1,
          "available": 1,
          "realigning": 0
        }
      }
    },
    {
      "contentType": "Destination",
      "buildingId": "D:-12:-3",
      "contentData": {
        "groupIndex": 0,
        "totalDemand": 1,
        "demand": {
          "total": 1,
          "unassigned": 0,
          "assigned": 1,
          "unassignedByGroup": {},
          "assignedByGroup": { "0": 1 },
          "maximumBeforeOvercrowding": 6,
          "isOvercrowding": false,
          "overcrowdingTime": 0.0,
          "overcrowdingSpeed": 0.0,
          "generationTimer": 3.1,
          "totalServiced": 0
        }
      }
    }
  ]
}
```

Destinations generate and hold demand. Houses own and supply vehicles. A
demand pin is associated with a group until dispatch; only then does the
vehicle establish a concrete house-to-destination relationship. After service,
the same vehicle returns to its owning house. See the
[snapshot development roadmap](docs/snapshot-roadmap.md) for the detailed
semantics, completed phases, and intentionally deferred pathfinding data.

Normal games use the fixed simulation seed `20261014`. The seed actually used
to initialize the current game is returned as `initialSeed` by `/snapshot` and
`/game/status`. Daily and weekly challenge seeds are left unchanged. Changing
the constant requires rebuilding the plugin; runtime configuration is planned
for a later GUI/configuration update.

When the game is not updating its main thread, a request returns HTTP 503 after
five seconds. A valid snapshot with `tileCount: 0` means that no current tilemap
was found yet; clients should wait for `phase: "playing"` and a nonzero count.

Run the Python validation script from the `MiniMotorwayAPI` directory while
the game is open. It uses only the standard library:

```powershell
python tests/apitest.py
```

The default run checks all five read endpoints without changing the game. It
also verifies demand totals, per-group counts, vehicle IDs and references,
house vehicle counts, and the relationship between assigned demand and
outbound vehicles.

To test the visible result, resume the game and choose two adjacent empty tiles
(the example uses `(0, 0)` and `(1, 0)`). Run these commands separately:

```powershell
python tests/apitest.py --add-road 0 0 East
python tests/apitest.py --inspect-road 0 0 East
python tests/apitest.py --clear-road 0 0 East
```

The add command leaves the road in place so it can be inspected in the game.
The clear command only accepts an isolated road with no other connections on
either tile. Both commands print the before and after tile states, concrete
count, and executed command count. The script exits with code 0 on success
and 1 on validation failure.

`POST /actions/road` takes JSON such as
`{"x": 5, "y": 7, "direction": "East"}`. Directions are `North`,
`NorthEast`, `East`, `SouthEast`, `South`, `SouthWest`, `West`, and `NorthWest`.
HTTP 202 means the game accepted the command for scheduling; it does not mean
the simulation has already applied it. Read a later `/snapshot` to verify
`editCommandsExecuted`, the resulting road, and resource count. Invalid edits return HTTP 422 with the
game's `resultCode`. Successful no-op edits return HTTP 200 with
`scheduled: false`.

`POST /game/restart` takes no body and returns HTTP 202 after the current map
accepts the restart request. It works during an active game and from the
game-over screen. Poll `/game/status` until `phase` is `playing` and the new
game has a nonzero `tileCount`. Restart discards the current run.

`POST /game/pause` and `POST /game/resume` also take no body. They return the
previous and current `timeScaleMode`. Resume always selects normal play speed,
even if the game was previously fast-forwarding. Pausing does not open the
pause menu, so snapshots remain available for an agent to inspect. Road edits
accepted while paused remain scheduled until simulation resumes.

The three bodyless control endpoints accept both explicitly framed empty
requests (`Content-Length: 0`) and HTTP/1.1 POST requests that omit both
`Content-Length` and `Transfer-Encoding`, including `curl -X POST`. This is a
scoped compatibility exception for Mono's otherwise automatic HTTP 411
response; JSON action endpoints still require normal request framing.

Control responses are sent as complete, non-streamed JSON bodies and close the
HTTP connection after each request. This avoids relying on persistent-connection
flush behavior in Mono's `HttpListener` while a control action changes game state.

`POST /actions/clear` takes `{"x": 5, "y": 7}` and returns HTTP 202 when the
edit is scheduled.

The optional state-changing smoke tests are explicit and never run as part of
the default read-only validation:

```powershell
python tests/apitest.py --pause
python tests/apitest.py --resume
python tests/apitest.py --restart
```

## Agent and reinforcement-learning development status

The API is currently suitable for manual debugging and early, low-frequency
agent experiments. It is not yet a deterministic Gym-style environment.

### P0: required for unattended agents

- [ ] Give queued actions deadlines, request IDs, and idempotency. A request
  that times out at the HTTP layer currently remains queued and may execute
  after the client has already received HTTP 503.
- [x] Allow restarting the current map without waiting for game over.
- [x] Allow an agent to pause for observation and resume at normal speed.
- [ ] Add a controlled run primitive. The initial design is to resume from a
  paused state, advance by a requested amount of **simulation time**, pause at
  the first eligible boundary, and return one snapshot. Wall-clock sleeps are
  intentionally avoided because frame rate and HTTP latency make them
  nondeterministic.
- [ ] Complete the action space: weekly upgrade selection, traffic lights,
  roundabouts, motorways, batch edits, and an explicit no-op.
- [ ] Add episode and execution identity: `episodeId`, `simulationTick`,
  `actionId`, final per-action results, and a terminal reason.
- [ ] Expand observations for high-score play with map bounds and terrain,
  vehicle or road-segment traffic load, motorway endpoints, and legal-action
  masks.

### P1: training reliability and throughput

- [ ] Capture at most one immutable observation per simulation boundary and
  move JSON serialization off the Unity main thread.
- [ ] Add `/v1/health`, `/v1/version`, `/v1/capabilities`, and a versioned JSON
  schema or OpenAPI contract.
- [ ] Add replay determinism, timeout/idempotency, reset-loop, concurrency, and
  long-running soak tests.
- [ ] Support configurable ports and isolated configuration for multiple game
  processes.

### P2: repository and deployment hygiene

- [ ] Replace machine-specific game paths and live-plugin output defaults in
  the project file with an explicit `GameDir`/staging configuration.
- [ ] Remove tracked IDE state, user project settings, old binaries, and
  vendored package artifacts; restore dependencies during build instead.
- [ ] Split the monolithic plugin into transport, game-control, snapshot, and
  contract components, then add CI compile and contract validation.

## Current limitations

- Road addition and clearing were verified in a running game: each command
  executed, changed both endpoint tiles, and adjusted the concrete count.
- Active-game restart and pause/resume were verified in a running game. Their
  JSON response and bodyless-POST compatibility are covered by the API smoke
  test options documented above.
- Additional upgrades and controlled simulation-time stepping are not implemented.
- The game phase is inferred from active UI screens and may be ambiguous during
  transitional loading frames.
- Vehicle positions, complete lane paths, route costs, and ETA are not in the snapshot.
