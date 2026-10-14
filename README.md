# Moretoways

BepInEx plugin for observing and controlling a running Mini Motorways game
through a local HTTP API. Road edits, road clearing, and restart are implemented; stepping
and other upgrades are still under development.

## Build

The project currently references the local Mono game and BepInEx assemblies in
`Moretoways.csproj`. Update those paths if the game is installed elsewhere.
Override `OutputPath` to build without writing directly into the game directory:

```powershell
dotnet msbuild Moretoways.csproj /t:Build /p:Configuration=Debug /p:OutputPath=bin\Debug\
```

Copy the built `Moretoways.dll` and `Newtonsoft.Json.dll` into the game's
`BepInEx/plugins` directory if Newtonsoft.Json is not already available there.

## Read API

The listener accepts requests on `localhost:9000`.
Each request is passed to Unity's main thread; the server
thread never reads live Unity or game objects.

| Endpoint | Response |
| --- | --- |
| `GET /snapshot` | Full game state snapshot. `/` is an alias. |
| `GET /tiles` | Tiles from a single snapshot. |
| `GET /tiles/count` | Tile count. |
| `GET /destinations` | One entry per destination building, including `groupIndex`, demand, footprint, and carpark coordinates. |
| `GET /game/status` | Phase, score, clock, available resources, and readiness flags. |
| `POST /actions/road` | Add one road connection from `(x, y)` in `direction`. |
| `POST /actions/clear` | Clear roads on tile `(x, y)`, respecting the game's permanence rules. |
| `POST /game/restart` | Restart from an active game-over screen. |

`/snapshot` includes a monotonically increasing `snapshotId`, UTC timestamp,
phase (`menu`, `playing`, or `gameOver`), time scale mode, score, clock, available upgrades, and
`editCommandsExecuted` (a diagnostic count of simulation edit commands), and
tile list. Each tile contains its coordinates, content type and type-specific
data, directional road states, and traffic light / roundabout information.
Destination buildings occupy multiple tiles; tiles sharing a `buildingId`
represent one building. `/destinations` returns each building once.
`groupIndex` is the stable identifier to use for matching houses and
destinations; its display color may vary by city.

When the game is not updating its main thread, a request returns HTTP 503 after
five seconds. A valid snapshot with `tileCount: 0` means that no current tilemap
was found yet; clients should wait for `phase: "playing"` and a nonzero count.

Run the Python validation script from the `MiniMotorwayAPI` directory while
the game is open. It uses only the standard library:

```powershell
python tests/apitest.py
```

The default run checks all five read endpoints without changing the game.
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

`POST /game/restart` takes no body and returns HTTP 202 after the game-over
screen accepts the restart request. Poll `/game/status` until `phase` is
`playing`. `POST /actions/clear` takes `{"x": 5, "y": 7}` and also returns
HTTP 202 when the edit is scheduled.

## Current limitations

- Road addition and clearing were verified in a running game: each command
  executed, changed both endpoint tiles, and adjusted the concrete count.
  Restart is restricted to the game-over screen and still needs a live smoke test.
- Additional upgrades, time control, and restart during an active game are not implemented.
- The game phase is inferred from active UI screens and requires live validation.
- Full vehicle locations and per-destination timer state are not in the snapshot yet.
