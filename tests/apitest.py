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

ACTION_STATES = {"queued", "scheduled", "executed", "rejected", "expired"}
EVENT_REASONS = {
    "houseAdded",
    "destinationAdded",
    "upgradeAvailable",
    "overcrowdingWarning",
    "overcrowdingStarted",
    "terminal",
    "interval",
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


def poll_action(base_url, action_id, timeout, expected_state="executed"):
    check(isinstance(action_id, str) and action_id, "Action response is missing actionId")
    deadline = time.monotonic() + timeout
    while True:
        action = request(base_url, f"/actions?id={action_id}")
        check(action.get("actionId") == action_id, "/actions: actionId mismatch")
        check(action.get("state") in ACTION_STATES, "/actions: invalid state")
        if action["state"] == expected_state:
            return action
        if action["state"] in {"rejected", "expired"}:
            raise ValidationError(f"Action ended as {action['state']}: {action}")
        if time.monotonic() >= deadline:
            raise ValidationError(
                f"Timed out waiting for action {action_id}; last state={action['state']}"
            )
        time.sleep(0.1)


def validate_read_api(base_url):
    preflight = request(base_url, "/game/pause", method="OPTIONS")
    snapshot = request(base_url, "/snapshot")
    status = request(base_url, "/game/status")
    upgrades_response = request(base_url, "/game/upgrades")
    maps_response = request(base_url, "/game/maps")
    tiles_response = request(base_url, "/tiles")
    count_response = request(base_url, "/tiles/count")
    destinations_response = request(base_url, "/destinations")
    events_response, event_headers = request(
        base_url, "/events?after=0", return_headers=True)
    decision_response = request(base_url, "/decision/status")
    legal_noop = request(base_url, "/actions/legal", body={"type": "noop"})

    check(snapshot.get("success") is True, "/snapshot: success is not true")
    check(preflight == {}, "OPTIONS /game/pause: expected an empty JSON object")
    check(isinstance(snapshot.get("snapshotId"), int), "/snapshot: missing snapshotId")
    check(isinstance(snapshot.get("editCommandsExecuted"), int),
          "/snapshot: missing editCommandsExecuted")
    if snapshot.get("phase") in {"playing", "gameOver"}:
        check(isinstance(snapshot.get("episodeId"), str) and snapshot["episodeId"],
              "/snapshot: missing episodeId for current game")
    if snapshot.get("phase") == "playing":
        check(isinstance(snapshot.get("initialSeed"), int),
              "/snapshot: missing initialSeed for active game")
        check(isinstance(snapshot.get("simulationTick"), int)
              and snapshot["simulationTick"] >= 0,
              "/snapshot: missing simulationTick for active game")
        check(isinstance(snapshot.get("simulationTime"), (int, float))
              and not isinstance(snapshot["simulationTime"], bool)
              and snapshot["simulationTime"] >= 0,
              "/snapshot: missing simulationTime for active game")
        bounds = snapshot.get("mapBounds")
        check(isinstance(bounds, dict)
              and isinstance(bounds.get("full"), dict)
              and isinstance(bounds.get("current"), dict),
              "/snapshot: missing map bounds for active game")
        for name in ("full", "current"):
            rectangle = bounds[name]
            check(all(isinstance(rectangle.get(field), (int, float))
                      and not isinstance(rectangle[field], bool)
                      for field in ("minX", "minY", "maxX", "maxY"))
                  and rectangle["minX"] < rectangle["maxX"]
                  and rectangle["minY"] < rectangle["maxY"],
                  f"/snapshot: invalid {name} map bounds")
    if snapshot.get("phase") == "gameOver":
        check(snapshot.get("terminalReason") in {"overcrowding", "gameEnded"},
              "/snapshot: missing terminal reason for game over")
    check(snapshot["tileCount"] == len(snapshot["tiles"]), "/snapshot: tile count mismatch")
    vehicles = snapshot.get("vehicles")
    check(isinstance(vehicles, list), "/snapshot: missing vehicles")
    check(snapshot.get("vehicleCount") == len(vehicles),
          "/snapshot: vehicle count mismatch")
    motorways = snapshot.get("motorways")
    check(isinstance(motorways, list) and
          snapshot.get("motorwayCount") == len(motorways),
          "/snapshot: motorway count mismatch")
    motorway_ids = [motorway.get("motorwayId") for motorway in motorways]
    check(all(isinstance(motorway_id, int) for motorway_id in motorway_ids)
          and len(motorway_ids) == len(set(motorway_ids)),
          "/snapshot: missing or duplicate motorway ID")
    for motorway in motorways:
        check(isinstance(motorway.get("hasConsumedUpgrade"), bool),
              "/snapshot: invalid motorway upgrade state")
        for endpoint in ("start", "end"):
            coordinates = motorway.get(endpoint + "Coordinates")
            check(isinstance(coordinates, dict) and
                  all(isinstance(coordinates.get(axis), int) for axis in ("x", "y"))
                  and motorway.get(endpoint + "Direction") in DIRECTIONS,
                  f"/snapshot: invalid motorway {endpoint} endpoint")
    check(status.get("success") is True
          and status.get("phase") in {"menu", "playing", "gameOver"},
          "/game/status: invalid phase")
    check(status.get("episodeId") == snapshot.get("episodeId"),
          "/game/status: episodeId differs from snapshot")
    for response_name, response in (("/tiles", tiles_response),
                                    ("/tiles/count", count_response),
                                    ("/destinations", destinations_response)):
        check(response.get("episodeId") == snapshot.get("episodeId"),
              f"{response_name}: episodeId differs from snapshot")
        if snapshot.get("phase") == "playing":
            check(isinstance(response.get("simulationTick"), int),
                  f"{response_name}: missing simulationTick")
    check(upgrades_response.get("success") is True
          and isinstance(upgrades_response.get("pending"), bool)
          and isinstance(upgrades_response.get("ready"), bool)
          and isinstance(upgrades_response.get("pendingCount"), int)
          and isinstance(upgrades_response.get("options"), list),
          "/game/upgrades: invalid response")
    for option in upgrades_response["options"]:
        check(isinstance(option.get("optionIndex"), int)
              and isinstance(option.get("type"), str)
              and isinstance(option.get("amount"), int)
              and isinstance(option.get("additionalConcrete"), int)
              and isinstance(option.get("selectable"), bool),
              "/game/upgrades: invalid option")
    maps = maps_response.get("maps")
    check(maps_response.get("success") is True and isinstance(maps, list)
          and all(isinstance(item.get("map"), str) and item["map"]
                  for item in maps), "/game/maps: invalid map list")
    check(tiles_response.get("count") == len(tiles_response.get("tiles", [])),
          "/tiles: count mismatch")
    check(isinstance(count_response.get("count"), int), "/tiles/count: invalid count")
    events = events_response.get("events")
    check(events_response.get("success") is True and isinstance(events, list)
          and isinstance(events_response.get("latestEventId"), int)
          and isinstance(events_response.get("gap"), bool),
          "/events: invalid event log response")
    check(event_headers.get("X-Snapshot-Capture-Ms") is None,
          "/events: lightweight query unexpectedly captured a snapshot")
    event_ids = [event.get("eventId") for event in events]
    check(event_ids == sorted(event_ids) and len(event_ids) == len(set(event_ids)),
          "/events: event IDs are not unique and ordered")
    for event in events:
        check(isinstance(event.get("episodeId"), str) and event["episodeId"],
              "/events: missing event episodeId")
        check(isinstance(event.get("reasons"), list) and event["reasons"]
              and all(reason in EVENT_REASONS for reason in event["reasons"]),
              "/events: invalid reason")
        check(isinstance(event.get("details"), list),
              "/events: invalid details")
    check(decision_response.get("success") is True
          and decision_response.get("mode") in {"off", "agent", "rl"}
          and isinstance(decision_response.get("minimumSimulationInterval"),
                         (int, float))
          and isinstance(decision_response.get("autoPauseReasons"), list)
          and isinstance(decision_response.get("pending"), bool),
          "/decision/status: invalid response")
    check(legal_noop.get("success") is True
          and legal_noop.get("type") == "noop"
          and isinstance(legal_noop.get("legal"), bool)
          and (snapshot.get("phase") != "playing" or legal_noop["legal"] is True)
          and "actionId" not in legal_noop,
          "/actions/legal: invalid no-op legality response")

    houses = [tile for tile in snapshot["tiles"]
              if tile.get("contentType") == "House"]
    house_ids = {house.get("buildingId") for house in houses}
    check(None not in house_ids and len(house_ids) == len(houses),
          "/snapshot: missing or duplicate house buildingId")

    snapshot_destinations = {}
    for tile in snapshot["tiles"]:
        terrain = tile.get("terrain")
        check(isinstance(terrain, dict)
              and all(isinstance(terrain.get(field), bool) for field in (
                  "buildable", "zoneable", "driveable", "water", "mountain",
                  "currentlyPlayable")),
              "/snapshot: invalid tile terrain")
        traffic = tile.get("traffic")
        if traffic is not None:
            check(all(isinstance(traffic.get(field), int) and traffic[field] >= 0
                      for field in ("laneCount", "vehicleCount",
                                    "stoppedVehicleCount"))
                  and isinstance(traffic.get("averageSpeed"), (int, float))
                  and traffic["averageSpeed"] >= 0
                  and traffic["stoppedVehicleCount"] <= traffic["vehicleCount"],
                  "/snapshot: invalid tile traffic load")
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
        "episodeId": snapshot.get("episodeId"),
        "simulationTick": snapshot.get("simulationTick"),
        "simulationTime": snapshot.get("simulationTime"),
        "editCommandsExecuted": snapshot["editCommandsExecuted"],
        "score": snapshot.get("score"),
        "initialSeed": snapshot.get("initialSeed"),
        "map": snapshot.get("map"),
        "availableMaps": len(maps),
        "tileCount": snapshot["tileCount"],
        "vehicleCount": snapshot.get("vehicleCount"),
        "motorwayCount": snapshot.get("motorwayCount"),
        "destinations": len(destinations),
        "pendingUpgrades": upgrades_response.get("pendingCount"),
        "latestEventId": events_response.get("latestEventId"),
        "decisionMode": decision_response.get("mode"),
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
    action_id = first.get("actionId")
    check(isinstance(action_id, str) and action_id,
          "Pause response did not include actionId")
    duplicate = request(base_url, "/game/pause", body={}, headers=headers)
    check(duplicate == first, "Duplicate request did not replay its original result")
    record = request(base_url, f"/requests?id={request_id}")
    check(record.get("state") == "complete", f"Request did not complete: {record}")
    check(record.get("responseStatusCode") == 200, f"Wrong recorded response: {record}")
    check(record.get("response") == first, "Recorded response differs from first result")
    check(record.get("actionId") == action_id,
          "Request record did not retain its actionId")
    action = poll_action(base_url, action_id, 2)
    check(action.get("requestId") == request_id,
          "Action record did not retain its requestId")
    check(action.get("path") == "/game/pause",
          "Action record has the wrong path")
    check(isinstance(action.get("executedTick"), int),
          "Executed action is missing its simulation tick")
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


def validate_decision_policy(base_url, poll_timeout):
    initial = request(base_url, "/snapshot")
    check(initial.get("phase") == "playing",
          "Decision policy check requires an active game")
    configured = request(base_url, "/decision/config", body={
        "mode": "rl",
        "minimumSimulationInterval": 0.05,
        "autoPauseReasons": [],
    })
    check(configured.get("mode") == "rl",
          f"Decision policy was not configured: {configured}")
    poll_action(base_url, configured.get("actionId"), 2)

    bodyless_post(base_url, "/game/resume")
    deadline = time.monotonic() + poll_timeout
    while True:
        decision = request(base_url, "/decision/status")
        if decision.get("pending"):
            break
        if time.monotonic() >= deadline:
            raise ValidationError(f"Timed out waiting for RL boundary: {decision}")
        time.sleep(0.05)
    check("interval" in decision.get("reasons", []),
          f"RL boundary has no interval reason: {decision}")
    first = request(base_url, "/snapshot")
    second = request(base_url, "/snapshot")
    check(first.get("timeScaleMode") == "Paused",
          "RL decision boundary did not pause the game")
    check(first.get("snapshotId") == second.get("snapshotId")
          and first.get("simulationTick") == second.get("simulationTick"),
          "Decision boundary did not reuse one immutable snapshot")
    check(decision.get("snapshotId") == first.get("snapshotId"),
          "Decision status and cached snapshot IDs differ")

    disabled = request(base_url, "/decision/config", body={"mode": "off"})
    check(disabled.get("mode") == "off" and disabled.get("pending") is False,
          f"Decision policy was not disabled: {disabled}")
    print(json.dumps({"boundary": decision, "snapshotId": first["snapshotId"]},
                     ensure_ascii=False, indent=2))
    print("DECISION POLICY PASS (game remains paused, policy disabled)")


def validate_agent_event_policy(base_url, poll_timeout):
    initial = request(base_url, "/snapshot")
    check(initial.get("phase") == "playing",
          "Agent event policy check requires an active game")
    latest_event_id = request(base_url, "/events?after=0")["latestEventId"]
    configured = request(base_url, "/decision/config", body={
        "mode": "agent",
        "autoPauseReasons": ["houseAdded"],
    })
    check(configured.get("mode") == "agent"
          and "houseAdded" in configured.get("autoPauseReasons", []),
          f"Agent event policy was not configured: {configured}")
    poll_action(base_url, configured.get("actionId"), 2)

    decision = None
    try:
        bodyless_post(base_url, "/game/resume")
        deadline = time.monotonic() + poll_timeout
        while True:
            decision = request(base_url, "/decision/status")
            if decision.get("pending") and "houseAdded" in decision.get("reasons", []):
                break
            if time.monotonic() >= deadline:
                raise ValidationError(
                    f"Timed out waiting for houseAdded boundary: {decision}")
            time.sleep(0.05)

        first = request(base_url, "/snapshot")
        second = request(base_url, "/snapshot")
        events = request(base_url, f"/events?after={latest_event_id}")["events"]
        matching_events = [
            event for event in events
            if event.get("episodeId") == first.get("episodeId")
            and "houseAdded" in event.get("reasons", [])
        ]
        check(first.get("timeScaleMode") == "Paused",
              f"Agent event boundary was published before pause: {first}")
        check(first.get("snapshotId") == second.get("snapshotId")
              and decision.get("snapshotId") == first.get("snapshotId"),
              "Agent event boundary did not reuse one immutable snapshot")
        check(matching_events,
              "Agent event boundary has no matching houseAdded event")
    finally:
        try:
            request(base_url, "/decision/config", body={"mode": "off"})
        finally:
            bodyless_post(base_url, "/game/pause")

    print(json.dumps({"boundary": decision, "snapshotId": first["snapshotId"]},
                     ensure_ascii=False, indent=2))
    print("AGENT EVENT POLICY PASS (game remains paused, policy disabled)")


def restart_game(base_url, seed, poll_timeout):
    initial = request(base_url, "/snapshot")
    check(initial["phase"] in {"playing", "gameOver"},
          "Restart requires an active or game-over game")
    if seed is None:
        result = bodyless_post(base_url, "/game/restart", expected_status=202)
        expected_seed = initial.get("initialSeed")
    else:
        check(0 <= seed <= 0xFFFFFFFF, "--seed must be a uint32 value")
        result = request(base_url, "/game/restart", body={"seed": seed},
                         expected_status=202)
        expected_seed = seed
    check(result.get("success") is True, f"Restart was not accepted: {result}")
    check(result.get("seed") == expected_seed,
          f"Restart accepted a different seed: {result}")
    action_id = result.get("actionId")
    restarted = poll_snapshot(
        base_url, poll_timeout,
        lambda snapshot: snapshot.get("phase") == "playing"
        and snapshot.get("snapshotId", 0) <= 5
        and snapshot.get("tileCount", 0) > 0
        and snapshot.get("initialSeed") == expected_seed,
        "a fresh playable game",
    )
    check(restarted.get("episodeId") != initial.get("episodeId"),
          "Restart did not create a new episodeId")
    action = poll_action(base_url, action_id, poll_timeout)
    check(action.get("resultingEpisodeId") == restarted.get("episodeId"),
          f"Restart action did not record the resulting episode: {action}")
    print(json.dumps({
        "response": result,
        "snapshotId": restarted["snapshotId"],
        "initialSeed": restarted.get("initialSeed"),
        "tileCount": restarted["tileCount"],
    }, ensure_ascii=False, indent=2))
    print("RESTART PASS")


def start_new_game(base_url, map_name, seed, start_paused, poll_timeout):
    initial_status = request(base_url, "/game/status")
    maps = request(base_url, "/game/maps")["maps"]
    check(map_name in {item["map"] for item in maps},
          f"Unknown map {map_name}; use GET /game/maps for valid names")
    payload = {"map": map_name, "startPaused": start_paused}
    if seed is not None:
        check(0 <= seed <= 0xFFFFFFFF, "--seed must be a uint32 value")
        payload["seed"] = seed
    result = request(base_url, "/game/new", body=payload, expected_status=202)
    check(result.get("success") is True, f"New game was not accepted: {result}")
    action_id = result.get("actionId")
    deadline = time.monotonic() + poll_timeout
    while True:
        status = request(base_url, "/game/status")
        launch = status.get("newGame") or {}
        check(launch.get("state") != "failed", f"New game failed: {launch}")
        if launch.get("state") == "ready":
            check(status.get("phase") == "playing" and status.get("tileCount", 0) > 0,
                  f"New game is not playable: {status}")
            check(status.get("map") == map_name and
                  status.get("initialSeed") == result["seed"],
                  f"New game map or seed differs: {status}")
            check(status.get("episodeId") != initial_status.get("episodeId"),
                  "New game did not create a new episodeId")
            action = poll_action(base_url, action_id, poll_timeout)
            check(action.get("resultingEpisodeId") == status.get("episodeId"),
                  f"New-game action did not record the resulting episode: {action}")
            print(json.dumps({"response": result, "status": status},
                             ensure_ascii=False, indent=2))
            print("NEW GAME PASS")
            return
        if time.monotonic() >= deadline:
            raise ValidationError(f"Timed out waiting for new game: {launch}")
        time.sleep(0.2)


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
    action_id = result.get("actionId")
    print(f"ADD RESPONSE: {result}")
    built = poll_snapshot(
        base_url, poll_timeout,
        lambda s: s["editCommandsExecuted"] > before_count
        and roads_at(s, x, y).get(direction) == "Active"
        and roads_at(s, nx, ny).get(opposite) == "Active",
        "road to become active on both tiles",
    )
    print_road_state("AFTER ADD", built, x, y, direction)
    action = poll_action(base_url, action_id, poll_timeout)
    check(action.get("scheduledCommands") == 1
          and action.get("executedCommands") == 1
          and action.get("failedCommands") == 0,
          f"Road action command counts are invalid: {action}")
    check(action.get("executedTick") is not None,
          f"Road action is missing execution time: {action}")
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
    action_id = result.get("actionId")
    print(f"CLEAR RESPONSE: {result}")
    cleared = poll_snapshot(
        base_url, poll_timeout,
        lambda s: s["editCommandsExecuted"] > before_count
        and not roads_at(s, x, y) and not roads_at(s, nx, ny),
        "test road to be cleared from both tiles",
    )
    print_road_state("AFTER CLEAR", cleared, x, y, direction)
    action = poll_action(base_url, action_id, poll_timeout)
    check(action.get("scheduledCommands") == 1
          and action.get("executedCommands") == 1
          and action.get("failedCommands") == 0,
          f"Clear action command counts are invalid: {action}")
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
    actions.add_argument("--new-game", metavar="MAP",
                         help="Start a new normal game on MAP (discards the current run)")
    actions.add_argument("--check-request-id", action="store_true",
                         help="Check idempotency via pause; leaves the game paused")
    actions.add_argument("--benchmark-snapshot", type=int, metavar="N",
                         help="Measure N read-only snapshot requests (1-100)")
    actions.add_argument("--check-decision-policy", action="store_true",
                         help="Run briefly until an RL boundary; leaves game paused")
    actions.add_argument("--check-agent-event-policy", action="store_true",
                         help="Run until houseAdded auto-pauses; leaves game paused")
    parser.add_argument("--poll-timeout", type=float, default=35,
                        help="Seconds to wait for an action (default: 35)")
    parser.add_argument("--seed", type=int,
                        help="uint32 seed for --restart or --new-game")
    parser.add_argument("--start-paused", action="store_true",
                        help="Start --new-game paused")
    args = parser.parse_args()
    try:
        check(args.poll_timeout > 0, "--poll-timeout must be positive")
        check(args.seed is None or args.restart or args.new_game,
              "--seed requires --restart or --new-game")
        check(args.new_game or not args.start_paused,
              "--start-paused requires --new-game")
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
            restart_game(base_url, args.seed, args.poll_timeout)
        elif args.new_game:
            start_new_game(base_url, args.new_game, args.seed,
                           args.start_paused, args.poll_timeout)
        elif args.check_request_id:
            validate_request_lifecycle(base_url)
        elif args.benchmark_snapshot is not None:
            benchmark_snapshots(base_url, args.benchmark_snapshot)
        elif args.check_decision_policy:
            validate_decision_policy(base_url, args.poll_timeout)
        elif args.check_agent_event_policy:
            validate_agent_event_policy(base_url, args.poll_timeout)
    except ValidationError as error:
        print(f"FAIL: {error}", file=sys.stderr)
        return 1
    print("PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
