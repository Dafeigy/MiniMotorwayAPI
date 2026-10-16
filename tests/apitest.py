"""Validate the Moretoways HTTP API against a running Mini Motorways game.

Read-only by default. Use --add-road and --clear-road separately to inspect
the game between edits. Uses only Python's standard library.
"""

import argparse
import json
import socket
import statistics
import sys
import time
import uuid
from urllib.error import HTTPError, URLError
from urllib.parse import urlsplit
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

VEHICLE_STATES = {
    "WaitingForDestination",
    "DrivingToDestination",
    "ParkingAtDestination",
    "ParkedAtDestination",
    "DrivingHome",
    "RealigningDriveway",
}


class ValidationError(Exception):
    pass


def check(condition, message):
    if not condition:
        raise ValidationError(message)


def request(base_url, path, *, body=None, method=None, expected_status=200,
            timeout=8, headers=None, return_headers=False):
    data = None if body is None else json.dumps(body).encode("utf-8")
    request_method = method or ("POST" if data is not None else "GET")
    request_headers = {"Content-Type": "application/json"} if data is not None else {}
    if headers:
        request_headers.update(headers)
    req = Request(
        base_url + path,
        data=data,
        headers=request_headers,
        method=request_method,
    )
    try:
        with urlopen(req, timeout=timeout) as response:
            status = response.status
            response_headers = response.headers
            payload = json.load(response)
    except HTTPError as error:
        status = error.code
        response_headers = error.headers
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
    return (payload, response_headers) if return_headers else payload


def bodyless_post(base_url, path, *, expected_status=200, timeout=8):
    """Send POST without Content-Length to cover Mono's former HTTP 411 path."""
    parsed = urlsplit(base_url)
    check(parsed.scheme == "http" and parsed.hostname,
          "Bodyless POST smoke tests require an http:// base URL")
    port = parsed.port or 80
    host_header = parsed.hostname if port == 80 else f"{parsed.hostname}:{port}"
    request_bytes = (
        f"POST {path} HTTP/1.1\r\n"
        f"Host: {host_header}\r\n"
        "Connection: close\r\n"
        "\r\n"
    ).encode("ascii")
    try:
        with socket.create_connection((parsed.hostname, port), timeout=timeout) as connection:
            connection.settimeout(timeout)
            connection.sendall(request_bytes)
            chunks = []
            while True:
                chunk = connection.recv(65536)
                if not chunk:
                    break
                chunks.append(chunk)
    except (OSError, TimeoutError) as error:
        raise ValidationError(f"{path}: raw POST failed: {error}") from error

    raw_response = b"".join(chunks)
    head, separator, body = raw_response.partition(b"\r\n\r\n")
    check(separator, f"{path}: invalid raw HTTP response")
    status_line = head.split(b"\r\n", 1)[0].decode("ascii", errors="replace")
    parts = status_line.split(" ", 2)
    check(len(parts) >= 2 and parts[1].isdigit(),
          f"{path}: invalid HTTP status line: {status_line}")
    status = int(parts[1])
    try:
        payload = json.loads(body.decode("utf-8"))
    except (ValueError, UnicodeError) as error:
        raise ValidationError(f"{path}: invalid JSON response: {error}") from error
    check(status == expected_status,
          f"{path}: HTTP {status}, expected {expected_status}: {payload}")
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
    preflight = request(base_url, "/game/pause", method="OPTIONS")
    snapshot = request(base_url, "/snapshot")
    status = request(base_url, "/game/status")
    tiles_response = request(base_url, "/tiles")
    count_response = request(base_url, "/tiles/count")
    destinations_response = request(base_url, "/destinations")

    check(snapshot.get("success") is True, "/snapshot: success is not true")
    check(preflight == {}, "OPTIONS /game/pause: expected an empty JSON object")
    check(isinstance(snapshot.get("snapshotId"), int), "/snapshot: missing snapshotId")
    check(isinstance(snapshot.get("editCommandsExecuted"), int),
          "/snapshot: missing editCommandsExecuted")
    if snapshot.get("phase") == "playing":
        check(isinstance(snapshot.get("initialSeed"), int),
              "/snapshot: missing initialSeed for active game")
    check(snapshot["tileCount"] == len(snapshot["tiles"]), "/snapshot: tile count mismatch")
    vehicles = snapshot.get("vehicles")
    check(isinstance(vehicles, list), "/snapshot: missing vehicles")
    check(snapshot.get("vehicleCount") == len(vehicles),
          "/snapshot: vehicle count mismatch")
    check(status.get("success") is True
          and status.get("phase") in {"menu", "playing", "gameOver"},
          "/game/status: invalid phase")
    check(tiles_response.get("count") == len(tiles_response.get("tiles", [])),
          "/tiles: count mismatch")
    check(isinstance(count_response.get("count"), int), "/tiles/count: invalid count")

    houses = [tile for tile in snapshot["tiles"]
              if tile.get("contentType") == "House"]
    house_ids = {house.get("buildingId") for house in houses}
    check(None not in house_ids and len(house_ids) == len(houses),
          "/snapshot: missing or duplicate house buildingId")

    snapshot_destinations = {}
    for tile in snapshot["tiles"]:
        if tile.get("contentType") == "Destination":
            snapshot_destinations.setdefault(tile.get("buildingId"), tile)
    destination_ids = set(snapshot_destinations)
    check(None not in destination_ids, "/snapshot: missing destination buildingId")

    vehicle_ids = [vehicle.get("vehicleId") for vehicle in vehicles]
    check(all(isinstance(vehicle_id, int) for vehicle_id in vehicle_ids)
          and len(vehicle_ids) == len(set(vehicle_ids)),
          "/snapshot: missing or duplicate vehicleId")
    for vehicle in vehicles:
        check(vehicle.get("state") in VEHICLE_STATES,
              "/snapshot: invalid vehicle state")
        check(vehicle.get("houseBuildingId") in house_ids,
              "/snapshot: vehicle references an unknown house")
        destination_id = vehicle.get("destinationBuildingId")
        check(destination_id is None or destination_id in destination_ids,
              "/snapshot: vehicle references an unknown destination")
        last_destination_id = vehicle.get("lastVisitedDestinationBuildingId")
        check(last_destination_id is None or isinstance(last_destination_id, str),
              "/snapshot: invalid last visited destination")

    vehicles_by_house = {}
    for vehicle in vehicles:
        vehicles_by_house.setdefault(vehicle["houseBuildingId"], []).append(vehicle)
    for house in houses:
        data = house.get("contentData") or {}
        counts = data.get("vehicleCounts")
        check(isinstance(counts, dict), "/snapshot: missing house vehicleCounts")
        for field in ("owned", "waiting", "available", "realigning"):
            check(isinstance(counts.get(field), int) and counts[field] >= 0,
                  f"/snapshot: invalid house vehicleCounts.{field}")
        check(counts["waiting"] <= counts["owned"]
              and counts["available"] <= counts["waiting"]
              and counts["realigning"] <= counts["owned"],
              "/snapshot: inconsistent house vehicle counts")
        check(data.get("hasWaitingVehicle") is (counts["waiting"] > 0),
              "/snapshot: house waiting state mismatch")
        house_vehicles = vehicles_by_house.get(house["buildingId"], [])
        check(counts["owned"] == len(house_vehicles),
              "/snapshot: house owned vehicle count mismatch")
        check(counts["waiting"] == sum(
            vehicle["state"] == "WaitingForDestination"
            for vehicle in house_vehicles
        ), "/snapshot: house waiting vehicle count mismatch")
        check(counts["realigning"] == sum(
            vehicle["state"] == "RealigningDriveway"
            for vehicle in house_vehicles
        ), "/snapshot: house realigning vehicle count mismatch")

    vehicles_by_destination = {}
    for vehicle in vehicles:
        destination_id = vehicle.get("destinationBuildingId")
        if destination_id is not None:
            vehicles_by_destination[destination_id] = (
                vehicles_by_destination.get(destination_id, 0) + 1
            )
    for destination_id, destination in snapshot_destinations.items():
        demand = (destination.get("contentData") or {}).get("demand") or {}
        check(demand.get("assigned") == vehicles_by_destination.get(destination_id, 0),
              "/snapshot: assigned demand and vehicle relationship mismatch")

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
        demand = data.get("demand")
        check(isinstance(demand, dict), "/destinations: missing demand detail")
        for field in ("total", "unassigned", "assigned",
                      "maximumBeforeOvercrowding", "totalServiced"):
            check(isinstance(demand.get(field), int) and demand[field] >= 0,
                  f"/destinations: invalid demand.{field}")
        check(demand["total"] == data["totalDemand"],
              "/destinations: detailed and legacy demand totals differ")
        check(demand["total"] == demand["unassigned"] + demand["assigned"],
              "/destinations: demand total mismatch")
        check(demand.get("isOvercrowding") is data.get("isOvercrowding"),
              "/destinations: overcrowding states differ")
        for field in ("overcrowdingTime", "overcrowdingSpeed", "generationTimer"):
            check(isinstance(demand.get(field), (int, float))
                  and not isinstance(demand[field], bool),
                  f"/destinations: invalid demand.{field}")
        for field, expected in (("unassignedByGroup", demand["unassigned"]),
                                ("assignedByGroup", demand["assigned"])):
            groups = demand.get(field)
            check(isinstance(groups, dict), f"/destinations: invalid demand.{field}")
            check(all(isinstance(key, str) and key.lstrip("-").isdigit()
                      and isinstance(value, int) and value >= 0
                      for key, value in groups.items()),
                  f"/destinations: invalid demand.{field} entry")
            check(sum(groups.values()) == expected,
                  f"/destinations: demand.{field} total mismatch")

    print("READ API PASS")
    print(json.dumps({
        "phase": snapshot["phase"],
        "timeScaleMode": snapshot.get("timeScaleMode"),
        "snapshotId": snapshot["snapshotId"],
        "editCommandsExecuted": snapshot["editCommandsExecuted"],
        "score": snapshot.get("score"),
        "initialSeed": snapshot.get("initialSeed"),
        "tileCount": snapshot["tileCount"],
        "vehicleCount": snapshot.get("vehicleCount"),
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


def set_paused(base_url, paused):
    initial = request(base_url, "/snapshot")
    check(initial["phase"] == "playing", "Time control requires an active game")
    path = "/game/pause" if paused else "/game/resume"
    expected_mode = "Paused" if paused else "Play"
    result = bodyless_post(base_url, path)
    check(result.get("success") is True, f"{path}: operation failed: {result}")
    # The game applies the time-scale change after this HTTP response is
    # created, so currentMode in the immediate response can still be old.
    observed = poll_snapshot(
        base_url, 2,
        lambda snapshot: snapshot.get("timeScaleMode") == expected_mode,
        f"time scale mode to become {expected_mode}",
    )
    print(json.dumps({"response": result, "snapshotId": observed["snapshotId"]},
                     ensure_ascii=False, indent=2))
    print(("PAUSE" if paused else "RESUME") + " PASS")


def validate_request_lifecycle(base_url):
    """State-changing opt-in check; leaves the game paused."""
    initial = request(base_url, "/snapshot")
    check(initial["phase"] == "playing", "Request ID check requires an active game")
    request_id = uuid.uuid4().hex
    headers = {"X-Request-Id": request_id}
    first, response_headers = request(
        base_url, "/game/pause", body={}, headers=headers, return_headers=True)
    check(response_headers.get("X-Request-Id") == request_id,
          "Pause response did not echo X-Request-Id")
    duplicate = request(base_url, "/game/pause", body={}, headers=headers)
    check(duplicate == first, "Duplicate request did not replay its original result")
    record = request(base_url, f"/requests?id={request_id}")
    check(record.get("state") == "complete", f"Request did not complete: {record}")
    check(record.get("responseStatusCode") == 200, f"Wrong recorded response: {record}")
    check(record.get("response") == first, "Recorded response differs from first result")
    conflict = request(base_url, "/game/resume", body={}, headers=headers,
                       expected_status=409)
    check(conflict.get("success") is False, "Reused ID was not rejected")
    print("REQUEST ID PASS (game remains paused)")


def benchmark_snapshots(base_url, samples):
    """Measure read-only snapshot costs reported by the plugin and client."""
    check(1 <= samples <= 100, "Snapshot sample count must be between 1 and 100")
    timings = {"captureMs": [], "serializeMs": [], "httpMs": [], "bytes": []}
    for _ in range(samples):
        started = time.perf_counter()
        _, headers = request(base_url, "/snapshot", return_headers=True)
        timings["httpMs"].append((time.perf_counter() - started) * 1000)
        for key, header in (("captureMs", "X-Snapshot-Capture-Ms"),
                            ("serializeMs", "X-Snapshot-Serialize-Ms")):
            value = headers.get(header)
            check(value is not None, f"/snapshot: missing {header}")
            timings[key].append(float(value))
        timings["bytes"].append(int(headers.get("Content-Length", "0")))

    summary = {}
    for key, values in timings.items():
        sorted_values = sorted(values)
        summary[key] = {
            "median": round(statistics.median(sorted_values), 3),
            "p95": round(sorted_values[(len(sorted_values) * 95 + 99) // 100 - 1], 3),
            "max": round(sorted_values[-1], 3),
        }
    print(json.dumps({"samples": samples, "timings": summary}, indent=2))


def restart_game(base_url, poll_timeout):
    initial = request(base_url, "/snapshot")
    check(initial["phase"] in {"playing", "gameOver"},
          "Restart requires an active or game-over game")
    result = bodyless_post(base_url, "/game/restart", expected_status=202)
    check(result.get("success") is True, f"Restart was not accepted: {result}")
    restarted = poll_snapshot(
        base_url, poll_timeout,
        lambda snapshot: snapshot.get("phase") == "playing"
        and snapshot.get("snapshotId", 0) <= 5
        and snapshot.get("tileCount", 0) > 0,
        "a fresh playable game",
    )
    print(json.dumps({
        "response": result,
        "snapshotId": restarted["snapshotId"],
        "initialSeed": restarted.get("initialSeed"),
        "tileCount": restarted["tileCount"],
    }, ensure_ascii=False, indent=2))
    print("RESTART PASS")


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
    actions.add_argument("--pause", action="store_true",
                         help="Pause the active game and leave it paused")
    actions.add_argument("--resume", action="store_true",
                         help="Resume the active game at normal speed")
    actions.add_argument("--restart", action="store_true",
                         help="Restart the current game (destructive)")
    actions.add_argument("--check-request-id", action="store_true",
                         help="Check idempotency via pause; leaves the game paused")
    actions.add_argument("--benchmark-snapshot", type=int, metavar="N",
                         help="Measure N read-only snapshot requests (1-100)")
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
        elif args.pause:
            set_paused(base_url, True)
        elif args.resume:
            set_paused(base_url, False)
        elif args.restart:
            restart_game(base_url, args.poll_timeout)
        elif args.check_request_id:
            validate_request_lifecycle(base_url)
        elif args.benchmark_snapshot is not None:
            benchmark_snapshots(base_url, args.benchmark_snapshot)
    except ValidationError as error:
        print(f"FAIL: {error}", file=sys.stderr)
        return 1
    print("PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
