"""Validate the Moretoways HTTP API against a running Mini Motorways game.

Read-only by default. Use --add-road and --clear-road separately to inspect
the game between edits. Uses only Python's standard library.
"""

import argparse
import json
import sys
import time
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen


DIRECTIONS = {
    "North": (0, 1, "South"),
    "NorthEast": (1, 1, "SouthWest"),
    "East": (1, 0, "West"),
    "SouthEast": (1, -1, "NorthWest"),
    "South": (0, -1, "North"),
    "SouthWest": (-1, -1, "NorthEast"),
    "West": (-1, 0, "East"),
    "NorthWest": (-1, 1, "SouthEast"),
}


class ValidationError(Exception):
    pass


def check(condition, message):
    if not condition:
        raise ValidationError(message)


def request(base_url, path, *, body=None, expected_status=200, timeout=8):
    data = None if body is None else json.dumps(body).encode("utf-8")
    req = Request(
        base_url + path,
        data=data,
        headers={"Content-Type": "application/json"} if data is not None else {},
        method="POST" if data is not None else "GET",
    )
    try:
        with urlopen(req, timeout=timeout) as response:
            status = response.status
            payload = json.load(response)
    except HTTPError as error:
        status = error.code
        try:
            payload = json.load(error)
        except (ValueError, UnicodeError):
            payload = {"error": error.reason}
    except (URLError, TimeoutError) as error:
        raise ValidationError(f"{path}: cannot reach plugin: {error}") from error
    except (ValueError, UnicodeError) as error:
        raise ValidationError(f"{path}: invalid JSON response: {error}") from error
    check(status == expected_status, f"{path}: HTTP {status}, expected {expected_status}: {payload}")
    check(isinstance(payload, dict), f"{path}: expected a JSON object")
    return payload


def tile_at(snapshot, x, y):
    return next(
        (tile for tile in snapshot["tiles"]
         if tile["coordinates"] == {"x": x, "y": y}),
        None,
    )


def roads_at(snapshot, x, y):
    tile = tile_at(snapshot, x, y)
    return {} if tile is None else tile["roads"]


def poll_snapshot(base_url, timeout, predicate, description):
    deadline = time.monotonic() + timeout
    while True:
        snapshot = request(base_url, "/snapshot")
        if predicate(snapshot):
            return snapshot
        if time.monotonic() >= deadline:
            raise ValidationError(
                f"Timed out waiting for {description}; last snapshotId={snapshot['snapshotId']}"
            )
        time.sleep(0.2)


def validate_read_api(base_url):
    snapshot = request(base_url, "/snapshot")
    status = request(base_url, "/game/status")
    tiles_response = request(base_url, "/tiles")
    count_response = request(base_url, "/tiles/count")
    destinations_response = request(base_url, "/destinations")

    check(snapshot.get("success") is True, "/snapshot: success is not true")
    check(isinstance(snapshot.get("snapshotId"), int), "/snapshot: missing snapshotId")
    check(isinstance(snapshot.get("editCommandsExecuted"), int),
          "/snapshot: missing editCommandsExecuted")
    if snapshot.get("phase") == "playing":
        check(isinstance(snapshot.get("initialSeed"), int),
              "/snapshot: missing initialSeed for active game")
    check(snapshot["tileCount"] == len(snapshot["tiles"]), "/snapshot: tile count mismatch")
    check(status.get("success") is True
          and status.get("phase") in {"menu", "playing", "gameOver"},
          "/game/status: invalid phase")
    check(tiles_response.get("count") == len(tiles_response.get("tiles", [])),
          "/tiles: count mismatch")
    check(isinstance(count_response.get("count"), int), "/tiles/count: invalid count")

    destinations = destinations_response.get("destinations", [])
    check(destinations_response.get("count") == len(destinations),
          "/destinations: count mismatch")
    ids = [item.get("buildingId") for item in destinations]
    check(all(ids) and len(ids) == len(set(ids)),
          "/destinations: missing or duplicate buildingId")
    for destination in destinations:
        check(destination.get("contentType") == "Destination",
              "/destinations: non-destination entry")
        data = destination.get("contentData") or {}
        check("groupIndex" in data and "totalDemand" in data and "carpark" in data,
              "/destinations: missing building data")

    print("READ API PASS")
    print(json.dumps({
        "phase": snapshot["phase"],
        "timeScaleMode": snapshot.get("timeScaleMode"),
        "snapshotId": snapshot["snapshotId"],
        "editCommandsExecuted": snapshot["editCommandsExecuted"],
        "score": snapshot.get("score"),
        "initialSeed": snapshot.get("initialSeed"),
        "tileCount": snapshot["tileCount"],
        "destinations": len(destinations),
        "resources": snapshot.get("resources"),
    }, ensure_ascii=False, indent=2))
    return snapshot


def road_state(snapshot, x, y, direction):
    dx, dy, opposite = DIRECTIONS[direction]
    nx, ny = x + dx, y + dy
    return {
        "snapshotId": snapshot["snapshotId"],
        "editCommandsExecuted": snapshot["editCommandsExecuted"],
        "concrete": (snapshot.get("resources") or {}).get("Concrete"),
        "source": {"x": x, "y": y, "tile": tile_at(snapshot, x, y)},
        "target": {"x": nx, "y": ny, "tile": tile_at(snapshot, nx, ny)},
    }


def print_road_state(label, snapshot, x, y, direction):
    print(f"{label}:")
    print(json.dumps(road_state(snapshot, x, y, direction),
                     ensure_ascii=False, indent=2))


def require_running(snapshot):
    check(snapshot["phase"] == "playing", "Road test requires an active game")
    check(snapshot.get("timeScaleMode") != "Paused",
          "Road test requires the game to be running")


def add_road(base_url, x, y, direction, poll_timeout):
    initial = request(base_url, "/snapshot")
    require_running(initial)
    dx, dy, opposite = DIRECTIONS[direction]
    nx, ny = x + dx, y + dy
    for tx, ty in ((x, y), (nx, ny)):
        tile = tile_at(initial, tx, ty)
        check(tile is None or (tile["contentType"] == "None" and not tile["roads"]),
              f"Test position ({tx}, {ty}) is occupied; choose two empty adjacent tiles")
    print_road_state("BEFORE ADD", initial, x, y, direction)
    before_count = initial["editCommandsExecuted"]
    result = request(base_url, "/actions/road",
                     body={"x": x, "y": y, "direction": direction},
                     expected_status=202)
    check(result.get("scheduled") is True, f"Road edit was not scheduled: {result}")
    print(f"ADD RESPONSE: {result}")
    built = poll_snapshot(
        base_url, poll_timeout,
        lambda s: s["editCommandsExecuted"] > before_count
        and roads_at(s, x, y).get(direction) == "Active"
        and roads_at(s, nx, ny).get(opposite) == "Active",
        "road to become active on both tiles",
    )
    print_road_state("AFTER ADD", built, x, y, direction)
    print("ROAD ADD PASS — road remains in the game until --clear-road is run")
    return built


def clear_road(base_url, x, y, direction, poll_timeout):
    initial = request(base_url, "/snapshot")
    require_running(initial)
    dx, dy, opposite = DIRECTIONS[direction]
    nx, ny = x + dx, y + dy
    source = tile_at(initial, x, y)
    target = tile_at(initial, nx, ny)
    check(source is not None and target is not None
          and source["contentType"] == "None" and target["contentType"] == "None"
          and source["roads"] == {direction: "Active"}
          and target["roads"] == {opposite: "Active"},
          "Expected one isolated active road on the two selected tiles; "
          "refusing to clear other roads")
    print_road_state("BEFORE CLEAR", initial, x, y, direction)
    before_count = initial["editCommandsExecuted"]
    result = request(base_url, "/actions/clear",
                     body={"x": x, "y": y}, expected_status=202)
    check(result.get("scheduled") is True, f"Clear edit was not scheduled: {result}")
    print(f"CLEAR RESPONSE: {result}")
    cleared = poll_snapshot(
        base_url, poll_timeout,
        lambda s: s["editCommandsExecuted"] > before_count
        and not roads_at(s, x, y) and not roads_at(s, nx, ny),
        "test road to be cleared from both tiles",
    )
    print_road_state("AFTER CLEAR", cleared, x, y, direction)
    print("ROAD CLEAR PASS")
    return cleared


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="http://localhost:9000",
                        help="Plugin URL")
    actions = parser.add_mutually_exclusive_group()
    actions.add_argument("--add-road", nargs=3, metavar=("X", "Y", "DIRECTION"),
                         help="Add a road and leave it in the game")
    actions.add_argument("--clear-road", nargs=3, metavar=("X", "Y", "DIRECTION"),
                         help="Clear one isolated road from the selected tiles")
    actions.add_argument("--inspect-road", nargs=3, metavar=("X", "Y", "DIRECTION"),
                         help="Show the current state of two adjacent tiles")
    parser.add_argument("--poll-timeout", type=float, default=10,
                        help="Seconds to wait for each road edit (default: 10)")
    args = parser.parse_args()
    try:
        base_url = args.base_url.rstrip("/")
        validate_read_api(base_url)
        selected = args.add_road or args.clear_road or args.inspect_road
        if selected:
            try:
                x, y = int(selected[0]), int(selected[1])
            except ValueError as error:
                raise ValidationError("X and Y must be integers") from error
            direction = selected[2]
            check(direction in DIRECTIONS,
                  f"Invalid direction: {direction}; choose from {', '.join(DIRECTIONS)}")
            check(args.poll_timeout > 0, "--poll-timeout must be positive")
            if args.add_road:
                add_road(base_url, x, y, direction, args.poll_timeout)
            elif args.clear_road:
                clear_road(base_url, x, y, direction, args.poll_timeout)
            else:
                print_road_state("CURRENT ROAD", request(base_url, "/snapshot"),
                                 x, y, direction)
    except ValidationError as error:
        print(f"FAIL: {error}", file=sys.stderr)
        return 1
    print("PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
