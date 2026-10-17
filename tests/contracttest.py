"""Static contract checks that do not require Mini Motorways to be installed."""

from pathlib import Path
import re
import sys


ROOT = Path(__file__).resolve().parents[1]

GET_ENDPOINTS = {
    "/",
    "/snapshot",
    "/tiles",
    "/tiles/count",
    "/destinations",
    "/game/status",
    "/game/maps",
    "/game/upgrades",
    "/requests",
    "/actions",
    "/events",
    "/decision/status",
}

POST_ENDPOINTS = {
    "/actions/legal",
    "/actions/road",
    "/actions/clear",
    "/actions/traffic-light",
    "/actions/roundabout",
    "/actions/motorway",
    "/actions/upgrade",
    "/actions/noop",
    "/decision/config",
    "/game/new",
    "/game/restart",
    "/game/pause",
    "/game/resume",
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


class ContractError(Exception):
    pass


def require(condition, message):
    if not condition:
        raise ContractError(message)


def read(relative_path):
    return (ROOT / relative_path).read_text(encoding="utf-8")


def main():
    source = read("Moretoways.cs")
    api = read("docs/api.md")
    readme = read("README.md")
    project = read("Moretoways.csproj")
    gitignore = read(".gitignore")

    source_paths = set(re.findall(r'"(/[a-z0-9?=\-/]*)"', source))
    for endpoint in sorted(GET_ENDPOINTS | POST_ENDPOINTS):
        require(endpoint in source_paths, f"Source does not route {endpoint}")
        documented = f"`{endpoint}" in api or f"`{endpoint}?" in api
        require(documented, f"docs/api.md does not document {endpoint}")
        overview = f"`GET {endpoint}" in readme or f"`POST {endpoint}" in readme
        require(overview, f"README endpoint table does not list {endpoint}")

    for state in ACTION_STATES:
        require(f'"{state}"' in source, f"Source does not expose action state {state}")
        require(f"`{state}`" in api, f"API docs do not describe action state {state}")

    for reason in EVENT_REASONS:
        require(f'"{reason}"' in source, f"Source does not emit event reason {reason}")
        require(f"`{reason}`" in api, f"API docs do not describe event reason {reason}")

    require("$(GameDir)" in project, "Project references do not use GameDir")
    require("MINI_MOTORWAYS_DIR" in project,
            "Project does not support MINI_MOTORWAYS_DIR")
    require("PackageReference Include=\"Newtonsoft.Json\"" in project,
            "Newtonsoft.Json is not restored through PackageReference")
    require(not re.search(r"[A-Za-z]:\\", project),
            "Project file contains an absolute Windows path")
    require("BepInEx\\plugins" not in project,
            "Project output still targets the live BepInEx plugin directory")

    for ignored in (".vs/", "*.user", "packages/"):
        require(ignored in gitignore, f".gitignore is missing {ignored}")
    for removed in (".vs", "packages", "Moretoways.csproj.user",
                    "Moretoways.previous.dll", "packages.config"):
        require(not (ROOT / removed).exists(), f"Obsolete artifact still exists: {removed}")

    compile(read("tests/apitest.py"), "tests/apitest.py", "exec")
    print("CONTRACT PASS")
    print(f"GET endpoints: {len(GET_ENDPOINTS)}")
    print(f"POST endpoints: {len(POST_ENDPOINTS)}")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except ContractError as error:
        print(f"CONTRACT FAIL: {error}", file=sys.stderr)
        sys.exit(1)
