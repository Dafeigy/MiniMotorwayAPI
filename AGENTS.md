# Moretoways Development Workflow

These instructions apply to the `MiniMotorwayAPI` repository.

## API documentation

`docs/api.md` is the default English reference for the currently implemented
local HTTP API; `docs/api.zh-CN.md` is its Simplified Chinese counterpart.
Whenever a change adds, removes, or changes an endpoint, request field, response
field, status code, header, or behavior, update both files in the same change.
Keep every English/Chinese documentation pair and the README API overview
consistent. Use `curl` for HTTP request examples. Verify the documentation
against the route handlers and snapshot serialization code; do not document
planned endpoints as implemented.

## Build and validation

After changing plugin code:

1. Run `git diff --check` and inspect the relevant diff.
2. Validate any changed Python test files without creating cache files, for example:
   `python -c "from pathlib import Path; compile(Path('tests/apitest.py').read_text(encoding='utf-8'), 'tests/apitest.py', 'exec')"`.
3. Build a Release plugin into a repository-local staging directory instead of writing directly to the game installation:
   `dotnet msbuild Moretoways.csproj /restore /t:Build /p:GameDir="F:\SteamLibrary\steamapps\common\Mini Motorways" /p:Configuration=Release /p:OutputPath=bin\DeployRelease\`.
4. Treat a successful build as compile verification only. Runtime behavior still needs to be checked in the game.

The current game installation is:

`F:\SteamLibrary\steamapps\common\Mini Motorways`

The BepInEx plugin directory is:

`F:\SteamLibrary\steamapps\common\Mini Motorways\BepInEx\plugins`

## Plugin deployment

After a successful Release build:

1. Check whether Mini Motorways is running. Do not terminate the game automatically. If it is running, explain that the loaded assembly cannot be refreshed and ask the user to exit the game before deployment if replacement is blocked.
2. Resolve and verify the exact source and destination paths before writing outside the repository.
3. Back up the installed `Moretoways.dll` and `Moretoways.pdb`, when present, beside the originals using a timestamped suffix such as `.backup-yyyyMMdd-HHmmss`.
4. Copy only the newly built `Moretoways.dll` and `Moretoways.pdb` from the staging directory into the BepInEx plugin directory. Do not copy referenced game assemblies or the staging directory's other dependencies.
5. Compare SHA-256 hashes of the staged and installed DLL/PDB to verify the replacement.
6. Report the deployed paths and backup filenames to the user.
7. Remind the user that BepInEx loads the plugin at game startup, so Mini Motorways must be fully restarted before the new plugin takes effect.

Writing to the game installation may require explicit sandbox approval. Request it rather than attempting to bypass filesystem restrictions.

## Runtime smoke test

After the user has restarted the game:

1. Inspect `BepInEx\LogOutput.log` for plugin startup or Harmony patch errors.
2. Run the read-only API validation from this repository with `python tests/apitest.py`.
3. For fixed-seed builds, start a new normal game and verify both of the following:
   - The BepInEx log contains `Using fixed simulation seed <seed>`.
   - `GET http://localhost:9000/snapshot` returns the same value in `initialSeed`.
4. Do not run road edits, restart actions, or other state-changing API tests unless the user has requested them or supplied a disposable test game.

