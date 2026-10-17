# `/snapshot` Development Roadmap

**English** | [简体中文](snapshot-roadmap.zh-CN.md)

This document records planned `/snapshot` extensions, game-entity semantics,
and explicit non-goals. New fields must remain additive and backward compatible;
existing field meanings must not be removed or changed.

## Entity and traffic semantics

### Destination

`DestinationModel` generates and holds passenger demand (pins). A vehicle that
arrives removes one assigned demand item and increases the score.

A destination owns or shares a `CarparkModel`, so it can receive and briefly
park vehicles and provide their exit path back to the owning house. It is not a
vehicle supplier and does not own vehicles that other demand can dispatch. Two
destinations can share one carpark.

### House

`HouseModel` owns and supplies vehicles. Houses and demand groups match through
`groupIndex`. When a destination has unassigned demand, dispatch selects a
reachable house in the same group with an available vehicle and the lowest path
cost.

Demand therefore begins with only a “demand group → destination” relationship;
it is not pre-bound to a house. A concrete “house → destination” trip exists
only after dispatch. After service, the vehicle returns to its owning house.

## Current API state

`/snapshot` already contains:

- `initialSeed`, the seed actually used to initialize the run.
- House `groupIndex` and `hasWaitingVehicle`.
- Destination `groupIndex`, `totalDemand`, `isOvercrowding`, upgrade state,
  footprint, and carpark entrance data.

The demand relationship is:

```text
totalDemand = unassignedDemandCount + assignedDemandCount
```

- `unassignedDemand`: demand without a selected vehicle.
- `waitingDemand`: demand with a dispatched vehicle that has not completed
  service. The API uses the clearer name `assignedDemand`.

`totalDemand` alone cannot distinguish these states or identify the house that
supplied a dispatched vehicle.

## Phase 1: demand detail (implemented and runtime verified)

Destination `contentData` includes demand detail while retaining legacy
`totalDemand` and `isOvercrowding`:

```json
{
  "demand": {
    "total": 4,
    "unassigned": 2,
    "assigned": 2,
    "unassignedByGroup": { "0": 2 },
    "assignedByGroup": { "0": 2 },
    "maximumBeforeOvercrowding": 6,
    "isOvercrowding": false,
    "overcrowdingTime": 0.0,
    "overcrowdingSpeed": 0.0,
    "generationTimer": 1.25,
    "totalServiced": 18
  }
}
```

At minimum, the representation exposes:

- Unassigned demand count.
- Assigned but incomplete demand count.
- Both counts grouped by the native dispatch group.
- The demand threshold that starts overcrowding timing.
- Current overcrowding time and speed.
- Total serviced demand.

`generationTimer` controls demand generation; it is not an overcrowding
countdown. Implementation and documentation must not conflate them.

## Phase 2: vehicles and active-trip relationships (implemented and runtime verified)

The top-level snapshot includes `vehicles`. The first version exposes passenger
relationships and behavior state without calculating full paths:

```json
{
  "vehicles": [
    {
      "vehicleId": 12,
      "state": "DrivingToDestination",
      "houseBuildingId": "H:-5:-1",
      "destinationBuildingId": "D:-12:-3",
      "lastVisitedDestinationBuildingId": null
    }
  ]
}
```

Requirements:

- `vehicleId` remains stable for the vehicle's lifetime.
- `houseBuildingId` always refers to the owning house.
- `destinationBuildingId` is populated only while the vehicle has an assigned destination.
- Return trips are represented by state and `lastVisitedDestinationBuildingId`;
  a destination must never be presented as the vehicle supplier.
- Clients can derive active outbound trips from
  `destinationBuildingId != null`. A duplicate `activeTrips` list is omitted to
  avoid inconsistent representations.

A future read-only `/vehicles` endpoint may be added for a concrete client need,
but it must use the same structure and `snapshotId` as its source snapshot.

## Phase 3: house vehicle counts (implemented and runtime verified)

House `contentData` retains `hasWaitingVehicle` and includes:

```json
{
  "vehicleCounts": {
    "owned": 2,
    "waiting": 1,
    "available": 1,
    "realigning": 0
  }
}
```

All values come from the same main-thread capture so house counts and the
top-level vehicle list remain consistent. Tests verify:

- Counts are nonnegative and do not exceed `owned`.
- `hasWaitingVehicle == (waiting > 0)`.
- Every vehicle's `houseBuildingId` resolves to a house in the snapshot.

## Seed and episode context

`initialSeed` is implemented. Potential additions for fuller context are:

- `city`.
- `gameMode`.
- `seedSource`, such as `fixedNormal` or `challenge`.
- `simulationFrame` for native-time identity; `snapshotId` counts API captures only.
- Explicit menu-stage lifecycle so a previous run's seed is never presented as current.

`initialSeed` is only the initialization seed, not the random generator's full
state. Exact replay still depends on game version, city, mode, challenge
configuration, and identical action timing.

## Current non-goals

The current scope excludes:

- A full path-cost matrix between every house and destination.
- Full lane-path serialization for every vehicle.
- Real-time position, speed, and ETA.
- Future-demand prediction or internal random-generator state.

A full path matrix would run pathfinding across many house/destination pairs and
could add substantial main-thread cost to frequent snapshot capture. It should
not be calculated by default without a concrete use case and performance budget.

## Acceptance principles

- Read all Unity/simulation objects only on the main thread.
- Add fields without breaking existing clients.
- `total == unassigned + assigned`.
- Grouped counts sum to their corresponding `unassigned` and `assigned` totals.
- Assigned demand and outbound vehicles targeting the destination agree within
  one simulation frame.
- Menu, playing, and game-over phases return explicit data or `null`, never
  stale prior-run state presented as current.
- Snapshot capture must not perform pathfinding or mutate simulation state.
