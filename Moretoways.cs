using BepInEx;
using BepInEx.Unity.Mono;
using HarmonyLib;
using Motorways;
using Motorways.Commands;
using Motorways.Models;
using Motorways.Views;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Moretoways
{
    [BepInPlugin("Nul4i.motoways.plugin", "Moretoways", "1.0.0")]
    public class Moretoways : BaseUnityPlugin
    {
        private const int Port = 9000;
        private const int RequestTimeoutMs = 5000;
        // Normal games otherwise consume a new value from Random.NextSimulationSeed()
        // every time they start. Keep this constant until a GUI/configuration surface
        // is added for choosing a seed at runtime.
        private const uint FixedSimulationSeed = 20261014u;

        private readonly ConcurrentQueue<SnapshotRequest> requests = new ConcurrentQueue<SnapshotRequest>();
        private HttpListener httpListener;
        private Thread httpThread;
        private volatile bool isRunning;
        private TilemapModel tilemap;
        private long snapshotId;
        private int editCommandsExecuted;
        private int lastScore;
        private ulong? initialSimulationSeed;
        private bool alwaysShowWorldGrid;
        private GameUIScreen gridGameUI;
        private TilemapView gridTilemapView;
        private GUIStyle gridCoordinateStyle;
        private GUIStyle gridCoordinateShadowStyle;
        private static Moretoways pluginInstance;

        private sealed class SnapshotRequest
        {
            public readonly string Path;
            public readonly string Body;
            public readonly TaskCompletionSource<ApiResponse> Completion =
                new TaskCompletionSource<ApiResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

            public SnapshotRequest(string path, string body)
            {
                Path = path;
                Body = body;
            }
        }

        private sealed class RoadRequest
        {
            public int? x { get; set; }
            public int? y { get; set; }
            public string direction { get; set; }
        }

        private sealed class ClearRequest
        {
            public int? x { get; set; }
            public int? y { get; set; }
        }

        private sealed class ApiResponse
        {
            public readonly int StatusCode;
            public readonly string Json;

            public ApiResponse(int statusCode, string json)
            {
                StatusCode = statusCode;
                Json = json;
            }
        }

        private sealed class TileSnapshot
        {
            public object coordinates;
            public string contentType;
            public string buildingId;
            public object contentData;
            public Dictionary<string, string> roads;
            public bool hasTrafficLight;
            public bool isRoundabout;
            public string drivewayDirection;
            public int unbuiltMotorwayId;
        }

        private sealed class GameSnapshot
        {
            public bool success = true;
            public long snapshotId;
            public int editCommandsExecuted;
            public DateTime timestamp;
            public string phase;
            public bool gameUI;
            public bool tilemapView;
            public bool camera;
            public string timeScaleMode;
            public ulong? initialSeed;
            public int? score;
            public object clock;
            public object resources;
            public int vehicleCount;
            public List<VehicleSnapshot> vehicles = new List<VehicleSnapshot>();
            public int tileCount;
            public List<TileSnapshot> tiles = new List<TileSnapshot>();
        }

        private sealed class VehicleSnapshot
        {
            public int vehicleId;
            public string state;
            public string houseBuildingId;
            public string destinationBuildingId;
            public string lastVisitedDestinationBuildingId;
        }

        private void Start()
        {
            pluginInstance = this;
            Harmony.CreateAndPatchAll(typeof(Moretoways));
            StartHttpServer();
            Logger.LogInfo("Moretoways API started");
        }

        private void StartHttpServer()
        {
            try
            {
                httpListener = new HttpListener();
                httpListener.Prefixes.Add("http://localhost:" + Port + "/");
                httpListener.Start();
                isRunning = true;
                httpThread = new Thread(ListenForRequests) { IsBackground = true };
                httpThread.Start();
            }
            catch (Exception exception)
            {
                Logger.LogError("Could not start HTTP server: " + exception);
                httpListener?.Close();
            }
        }

        private void ListenForRequests()
        {
            while (isRunning)
            {
                try
                {
                    ProcessRequest(httpListener.GetContext());
                }
                catch (HttpListenerException)
                {
                    if (isRunning)
                        Logger.LogError("HTTP listener stopped unexpectedly");
                }
                catch (ObjectDisposedException)
                {
                    if (isRunning)
                        Logger.LogError("HTTP listener disposed unexpectedly");
                }
                catch (Exception exception)
                {
                    if (isRunning)
                        Logger.LogError("HTTP listener error: " + exception);
                }
            }
        }

        private void ProcessRequest(HttpListenerContext context)
        {
            var response = context.Response;
            try
            {
                string path = context.Request.Url.AbsolutePath;
                bool isAction = path == "/actions/road" || path == "/actions/clear" ||
                    path == "/game/restart";
                if (context.Request.HttpMethod != (isAction ? "POST" : "GET"))
                {
                    SendJson(response, 405, "{\"error\":\"Method Not Allowed\"}");
                    return;
                }

                if (path != "/" && path != "/snapshot" && path != "/tiles" &&
                    path != "/tiles/count" && path != "/destinations" &&
                    path != "/game/status" && !isAction)
                {
                    SendJson(response, 404, "{\"error\":\"Not Found\"}");
                    return;
                }

                string body = null;
                if (path == "/actions/road" || path == "/actions/clear")
                {
                    if (context.Request.ContentLength64 > 4096)
                    {
                        SendJson(response, 413, "{\"error\":\"Request body too large\"}");
                        return;
                    }
                    using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                        body = reader.ReadToEnd();
                    if (body.Length > 4096)
                    {
                        SendJson(response, 413, "{\"error\":\"Request body too large\"}");
                        return;
                    }
                }

                var request = new SnapshotRequest(path, body);
                requests.Enqueue(request);
                if (!request.Completion.Task.Wait(RequestTimeoutMs))
                {
                    SendJson(response, 503, "{\"error\":\"Game main thread did not respond\"}");
                    return;
                }

                ApiResponse result = request.Completion.Task.Result;
                SendJson(response, result.StatusCode, result.Json);
            }
            catch (Exception exception)
            {
                Logger.LogError("HTTP request failed: " + exception);
                try
                {
                    SendJson(response, 500, "{\"error\":\"Internal Server Error\"}");
                }
                catch (Exception) { }
            }
            finally
            {
                response.Close();
            }
        }

        private static void SendJson(HttpListenerResponse response, int statusCode, string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.M) &&
                (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
            {
                ToggleWorldGridMode();
            }

            SnapshotRequest request;
            if (!requests.TryDequeue(out request))
                return;

            try
            {
                // Game objects are copied on Unity's main thread. The HTTP thread
                // receives JSON only and never accesses a live game object.
                if (request.Path == "/actions/road")
                    request.Completion.TrySetResult(AddRoad(request.Body));
                else if (request.Path == "/actions/clear")
                    request.Completion.TrySetResult(ClearRoads(request.Body));
                else if (request.Path == "/game/restart")
                    request.Completion.TrySetResult(RestartGame());
                else
                {
                    GameSnapshot snapshot = CaptureSnapshot();
                    request.Completion.TrySetResult(new ApiResponse(200, SerializeResponse(snapshot, request.Path)));
                }
            }
            catch (Exception exception)
            {
                Logger.LogError("Snapshot failed: " + exception);
                request.Completion.TrySetResult(new ApiResponse(500,
                    "{\"success\":false,\"error\":\"Snapshot failed\"}"));
            }
        }

        private void ToggleWorldGridMode()
        {
            alwaysShowWorldGrid = !alwaysShowWorldGrid;

            var gameUI = GetGridGameUI();
            if (gameUI != null)
                gameUI.SetWorldGridActive(alwaysShowWorldGrid, TransitionStyle.Tween);

            Logger.LogInfo("Always-show world grid: " +
                (alwaysShowWorldGrid ? "enabled" : "disabled"));
        }

        private GameUIScreen GetGridGameUI()
        {
            if (gridGameUI == null)
            {
                gridGameUI = UnityEngine.Object.FindObjectOfType<GameUIScreen>();
                gridTilemapView = null;
            }

            return gridGameUI;
        }

        private void OnGUI()
        {
            if (!alwaysShowWorldGrid || Event.current.type != EventType.Repaint)
                return;

            GameUIScreen gameUI = GetGridGameUI();
            if (gameUI == null)
                return;

            if (gridTilemapView == null)
            {
                gridTilemapView = Traverse.Create(gameUI)
                    .Field("_tilemapView").GetValue<TilemapView>();
            }
            if (gridTilemapView == null)
                return;

            TilemapModel currentTilemap = Traverse.Create(gridTilemapView)
                .Field("_tilemapModel").GetValue<TilemapModel>();
            if (currentTilemap == null)
                return;

            EnsureGridCoordinateStyles();
            int fontSize = Mathf.Clamp(
                Mathf.RoundToInt(gridTilemapView.ScreenDistanceBetweenTiles * 0.2f), 9, 16);
            gridCoordinateStyle.fontSize = fontSize;
            gridCoordinateShadowStyle.fontSize = fontSize;

            const float labelWidth = 72f;
            const float labelHeight = 22f;
            foreach (Vector2Int coordinates in currentTilemap.GetAllTileCoordinates())
            {
                Vector2 screenPosition =
                    gridTilemapView.GetScreenPositionFromTileCoordinates(coordinates);
                float guiX = screenPosition.x - labelWidth * 0.5f;
                float guiY = Screen.height - screenPosition.y - labelHeight * 0.5f;

                if (guiX + labelWidth < 0f || guiX > Screen.width ||
                    guiY + labelHeight < 0f || guiY > Screen.height)
                {
                    continue;
                }

                string label = "(" + coordinates.x + "," + coordinates.y + ")";
                GUI.Label(new Rect(guiX + 1f, guiY + 1f, labelWidth, labelHeight),
                    label, gridCoordinateShadowStyle);
                GUI.Label(new Rect(guiX, guiY, labelWidth, labelHeight),
                    label, gridCoordinateStyle);
            }
        }

        private void EnsureGridCoordinateStyles()
        {
            if (gridCoordinateStyle != null)
                return;

            gridCoordinateStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter
            };
            gridCoordinateStyle.normal.textColor = new Color(1f, 1f, 1f, 0.62f);

            gridCoordinateShadowStyle = new GUIStyle(gridCoordinateStyle);
            gridCoordinateShadowStyle.normal.textColor = new Color(0f, 0f, 0f, 0.38f);
        }

        private ApiResponse AddRoad(string body)
        {
            RoadRequest input;
            try
            {
                input = JsonConvert.DeserializeObject<RoadRequest>(body);
            }
            catch (JsonException)
            {
                return new ApiResponse(400, "{\"success\":false,\"error\":\"Invalid JSON\"}");
            }

            TileDirection direction;
            if (input == null || !input.x.HasValue || !input.y.HasValue ||
                !Enum.TryParse(input.direction, true, out direction) ||
                direction == TileDirection.None || (int)direction < 0 || (int)direction > 7)
            {
                return new ApiResponse(400,
                    "{\"success\":false,\"error\":\"Expected x, y and a compass direction\"}");
            }

            return ApplyTileEdit((editor, view) =>
                editor.AddRoad(view, new Vector2Int(input.x.Value, input.y.Value), direction));
        }

        private ApiResponse ClearRoads(string body)
        {
            ClearRequest input;
            try
            {
                input = JsonConvert.DeserializeObject<ClearRequest>(body);
            }
            catch (JsonException)
            {
                return new ApiResponse(400, "{\"success\":false,\"error\":\"Invalid JSON\"}");
            }

            if (input == null || !input.x.HasValue || !input.y.HasValue)
                return new ApiResponse(400, "{\"success\":false,\"error\":\"Expected x and y\"}");

            return ApplyTileEdit((editor, view) => editor.ClearTileExplicit(
                view, new Vector2Int(input.x.Value, input.y.Value),
                TileEditor.ClearTileOfType.Roads,
                Tile.TileChangePermissions.RespectPermanence));
        }

        private ApiResponse ApplyTileEdit(Func<TileEditor, TilemapView, TileEditResult> createEdit)
        {
            var gameUI = UnityEngine.Object.FindObjectOfType<GameUIScreen>();
            var tilemapView = gameUI == null ? null :
                Traverse.Create(gameUI).Field("_tilemapView").GetValue<TilemapView>();
            if (gameUI == null || tilemapView == null)
                return new ApiResponse(409, "{\"success\":false,\"error\":\"No active game\"}");

            var scope = Traverse.Create(tilemapView).Field("_scope").GetValue<Factory.IScope>();
            var simulation = Traverse.Create(tilemapView).Field("_simulation").GetValue<Server.ISimulation>();
            if (scope == null || simulation == null)
                return new ApiResponse(503,
                    "{\"success\":false,\"error\":\"Game action services are unavailable\"}");

            TileEditor editor = scope.Get<TileEditor>();
            ClientUpgradeDatabase clientUpgrades = scope.Get<ClientUpgradeDatabase>();
            if (editor == null || clientUpgrades == null)
                return new ApiResponse(503,
                    "{\"success\":false,\"error\":\"Game edit services are unavailable\"}");

            TileEditResult result = createEdit(editor, tilemapView);
            if (!result.IsSuccessful)
                return new ApiResponse(422, JsonConvert.SerializeObject(new
                {
                    success = false,
                    resultCode = result.resultCode.ToString(),
                    errorPosition = new { x = result.errorPosition.x, y = result.errorPosition.y }
                }));

            if (result.edit == null)
                return new ApiResponse(200, "{\"success\":true,\"scheduled\":false,\"resultCode\":\"NoChange\"}");

            // Match MotorwaysPlayerAction.AddTileEdit(Immediate): add the client
            // edit to the tile views, schedule the simulation command, then
            // track the pending edit in the client upgrade database.
            ClientTileEdit clientEdit = tilemapView.GenerateClientTileEditAndAddEditToViews(result.edit, false);
            bool scheduled = simulation.ScheduleCommand(EditTileCommand.Create(scope, result.edit));
            clientEdit.isScheduledOnSimulation = scheduled;
            if (scheduled)
                clientUpgrades.AddTileEdit(clientEdit);
            return new ApiResponse(scheduled ? 202 : 409, JsonConvert.SerializeObject(new
            {
                success = scheduled,
                scheduled = scheduled,
                resultCode = scheduled ? "Scheduled" : "ScheduleRejected"
            }));
        }

        private ApiResponse RestartGame()
        {
            var gameOver = UnityEngine.Object.FindObjectOfType<GameOverScreen>();
            if (gameOver == null)
                return new ApiResponse(409,
                    "{\"success\":false,\"error\":\"Restart is only available on the game-over screen\"}");

            gameOver.OnRestart();
            tilemap = null;
            return new ApiResponse(202, "{\"success\":true,\"resultCode\":\"RestartRequested\"}");
        }

        private GameSnapshot CaptureSnapshot()
        {
            var gameUI = UnityEngine.Object.FindObjectOfType<GameUIScreen>();
            var gameOverUI = UnityEngine.Object.FindObjectOfType<GameOverScreen>();
            var tilemapView = gameUI == null ? null :
                Traverse.Create(gameUI).Field("_tilemapView").GetValue<TilemapView>();
            var snapshot = new GameSnapshot
            {
                snapshotId = ++snapshotId,
                editCommandsExecuted = this.editCommandsExecuted,
                timestamp = DateTime.UtcNow,
                phase = gameOverUI != null ? "gameOver" : gameUI != null ? "playing" : "menu",
                gameUI = gameUI != null,
                tilemapView = tilemapView != null,
                camera = Camera.main != null,
                timeScaleMode = gameUI == null ? null : gameUI.GetTimeScaleMode().ToString(),
                initialSeed = initialSimulationSeed
            };

            if (gameUI == null)
            {
                tilemap = null;
                if (gameOverUI != null)
                    snapshot.score = lastScore;
                return snapshot;
            }

            // The view's model is the current game's tilemap. The Harmony hook
            // remains a fallback for frames in which the view has not bound yet.
            if (tilemapView != null)
            {
                var currentTilemap = Traverse.Create(tilemapView).Field("_tilemapModel").GetValue<TilemapModel>();
                if (currentTilemap != null)
                    tilemap = currentTilemap;
            }

            var scoreModel = Traverse.Create(gameUI).Field("_scoreModel").GetValue<ScoreModel>();
            if (scoreModel != null)
            {
                snapshot.score = scoreModel.Score;
                lastScore = scoreModel.Score;
            }

            var clockView = Traverse.Create(gameUI).Field("_clockView").GetValue<ClockView>();
            var clock = clockView != null ? clockView.ClockModel : null;
            if (clock != null)
                snapshot.clock = new { time = (double)clock.Time, hour = clock.Hour, day = clock.Day, week = clock.Week };

            var upgrades = Traverse.Create(gameUI).Field("_upgradeDatabaseModel").GetValue<UpgradeDatabaseModel>();
            if (upgrades != null)
            {
                var available = new Dictionary<string, int>();
                foreach (UpgradeType type in Enum.GetValues(typeof(UpgradeType)))
                {
                    if (type != UpgradeType.Count)
                        available[type.ToString()] = upgrades.GetAvailableUpgradeCount(type);
                }
                snapshot.resources = available;
            }

            var simulation = Traverse.Create(gameUI).Field("_simulation").GetValue<Server.ISimulation>();
            if (simulation != null)
            {
                var vehicleModels = simulation.GetModels<VehicleModel>();
                var vehicleEnumerator = vehicleModels.GetEnumerator();
                while (vehicleEnumerator.MoveNext())
                {
                    VehicleModel vehicle = vehicleEnumerator.Current;
                    if (vehicle != null)
                        snapshot.vehicles.Add(CaptureVehicle(vehicle));
                }
            }
            snapshot.vehicleCount = snapshot.vehicles.Count;

            if (tilemap != null)
            {
                var tiles = Traverse.Create(tilemap).Field("_tiles")
                    .GetValue<Dictionary<Vector2Int, TileModel>>();
                if (tiles != null)
                {
                    foreach (var entry in tiles)
                    {
                        if (entry.Value != null && entry.Value.Tile != null)
                            snapshot.tiles.Add(CaptureTile(entry.Key, entry.Value.Tile));
                    }
                }
            }

            snapshot.tileCount = snapshot.tiles.Count;
            return snapshot;
        }

        private static TileSnapshot CaptureTile(Vector2Int coordinates, Tile tile)
        {
            var roads = new Dictionary<string, string>();
            for (int directionIndex = 0; directionIndex < 8; directionIndex++)
            {
                var direction = (TileDirection)directionIndex;
                var state = tile.StateOfRoadInDirection(direction);
                if (state != RoadState.None)
                    roads[direction.ToString()] = state.ToString();
            }

            object contentData = null;
            string buildingId = null;
            if (tile.ContentType == TileContentType.House && tile.ContentModel is HouseModel house)
            {
                buildingId = "H:" + coordinates.x + ":" + coordinates.y;
                var houseData = Traverse.Create(house);
                var ownedVehicles = houseData.Field("ownedVehicles").GetValue<List<VehicleModel>>();
                var waitingVehicles = houseData.Field("waitingVehicles").GetValue<List<VehicleModel>>();
                var realigningVehicles = houseData.Field("realigningVehicles").GetValue<List<VehicleModel>>();
                contentData = new
                {
                    groupIndex = house.GroupIndex,
                    hasWaitingVehicle = house.HasWaitingVehicle,
                    vehicleCounts = new
                    {
                        owned = CountVehicles(ownedVehicles),
                        waiting = CountVehicles(waitingVehicles),
                        available = CountAvailableVehicles(waitingVehicles),
                        realigning = CountVehicles(realigningVehicles)
                    }
                };
            }
            else if (tile.ContentType == TileContentType.Destination &&
                     tile.ContentModel is DestinationModel destination)
            {
                int minX = coordinates.x;
                int minY = coordinates.y;
                var footprint = new List<object>();
                if (destination.TileModels != null)
                {
                    foreach (TileModel footprintTile in destination.TileModels)
                    {
                        if (footprintTile == null)
                            continue;
                        Vector2Int position = footprintTile.Coordinates;
                        minX = Math.Min(minX, position.x);
                        minY = Math.Min(minY, position.y);
                        footprint.Add(new { x = position.x, y = position.y });
                    }
                }
                buildingId = "D:" + minX + ":" + minY;
                var carpark = destination.Carpark;
                bool hasTopLeftEntrance = carpark != null &&
                    Traverse.Create(carpark).Field("entranceAtTopLeft").GetValue<bool>();
                bool hasBottomRightEntrance = carpark != null &&
                    Traverse.Create(carpark).Field("entranceAtBottomRight").GetValue<bool>();
                var destinationFrame = destination.CurrentFrame;
                int unassignedDemandCount = destination.unassignedDemand.Count;
                int assignedDemandCount = destination.waitingDemand.Count;
                contentData = new
                {
                    groupIndex = destination.GroupIndex,
                    totalDemand = destination.TotalDemand,
                    isOvercrowding = destination.IsOvercrowding,
                    isUpgraded = destination.IsUpgraded,
                    demand = new
                    {
                        total = destination.TotalDemand,
                        unassigned = unassignedDemandCount,
                        assigned = assignedDemandCount,
                        unassignedByGroup = CountDemandByGroup(destination.unassignedDemand),
                        assignedByGroup = CountDemandByGroup(destination.waitingDemand),
                        maximumBeforeOvercrowding = destination.MaximumDemandBeforeTimerStarts,
                        isOvercrowding = destination.IsOvercrowding,
                        overcrowdingTime = destinationFrame == null
                            ? (double?)null
                            : (double)destinationFrame.OvercrowdingTime,
                        overcrowdingSpeed = destinationFrame == null
                            ? (double?)null
                            : (double)destinationFrame.OvercrowdingSpeed,
                        generationTimer = (double)destination.demandTimer,
                        totalServiced = destination.totalServicedPins
                    },
                    footprint = footprint,
                    carpark = carpark == null ? null : new
                    {
                        topLeft = new { x = carpark.TopLeftWorldCoordinate.x, y = carpark.TopLeftWorldCoordinate.y },
                        drivewayTopLeft = hasTopLeftEntrance
                            ? (object)new { x = carpark.TopLeftDrivewayTileCoordinates.x, y = carpark.TopLeftDrivewayTileCoordinates.y }
                            : null,
                        drivewayBottomRight = hasBottomRightEntrance
                            ? (object)new { x = carpark.BottomRightDrivewayTileCoordinates.x, y = carpark.BottomRightDrivewayTileCoordinates.y }
                            : null
                    }
                };
            }

            return new TileSnapshot
            {
                coordinates = new { x = coordinates.x, y = coordinates.y },
                contentType = tile.ContentType.ToString(),
                buildingId = buildingId,
                contentData = contentData,
                roads = roads,
                hasTrafficLight = tile.HasTrafficLight,
                isRoundabout = tile.IsCenterOfRoundabout,
                drivewayDirection = tile.DrivewayDirection.ToString(),
                unbuiltMotorwayId = tile.UnbuiltMotorwayId
            };
        }

        private static VehicleSnapshot CaptureVehicle(VehicleModel vehicle)
        {
            var vehicleData = Traverse.Create(vehicle);
            var behaviorState = vehicleData.Field("behaviorState").GetValue<object>();
            var house = vehicleData.Field("house").GetValue<HouseModel>();
            var destination = vehicleData.Field("destination").GetValue<DestinationModel>();
            var lastVisitedDestination = vehicleData.Field("lastVisitedDestination")
                .GetValue<DestinationModel>();
            return new VehicleSnapshot
            {
                vehicleId = vehicleData.Field("id").GetValue<int>(),
                state = behaviorState == null ? null : behaviorState.ToString(),
                houseBuildingId = GetHouseBuildingId(house),
                destinationBuildingId = GetDestinationBuildingId(destination),
                lastVisitedDestinationBuildingId = GetDestinationBuildingId(lastVisitedDestination)
            };
        }

        private static string GetHouseBuildingId(HouseModel house)
        {
            if (house == null || house.tileModel == null)
                return null;

            Vector2Int coordinates = house.tileModel.Coordinates;
            return "H:" + coordinates.x + ":" + coordinates.y;
        }

        private static string GetDestinationBuildingId(DestinationModel destination)
        {
            if (destination == null || destination.TileModels == null)
                return null;

            bool foundCoordinates = false;
            int minX = 0;
            int minY = 0;
            foreach (TileModel tileModel in destination.TileModels)
            {
                if (tileModel == null)
                    continue;

                Vector2Int coordinates = tileModel.Coordinates;
                if (!foundCoordinates)
                {
                    minX = coordinates.x;
                    minY = coordinates.y;
                    foundCoordinates = true;
                }
                else
                {
                    minX = Math.Min(minX, coordinates.x);
                    minY = Math.Min(minY, coordinates.y);
                }
            }
            return foundCoordinates ? "D:" + minX + ":" + minY : null;
        }

        private static int CountVehicles(List<VehicleModel> vehicles)
        {
            return vehicles == null ? 0 : vehicles.Count;
        }

        private static int CountAvailableVehicles(List<VehicleModel> vehicles)
        {
            if (vehicles == null)
                return 0;

            int count = 0;
            foreach (VehicleModel vehicle in vehicles)
            {
                if (vehicle != null && vehicle.IsAvailableAtHouse)
                    count++;
            }
            return count;
        }

        private static Dictionary<int, int> CountDemandByGroup(IEnumerable<int> demandGroups)
        {
            var counts = new Dictionary<int, int>();
            if (demandGroups == null)
                return counts;

            foreach (int groupIndex in demandGroups)
            {
                int count;
                counts.TryGetValue(groupIndex, out count);
                counts[groupIndex] = count + 1;
            }
            return counts;
        }

        private static string SerializeResponse(GameSnapshot snapshot, string path)
        {
            switch (path)
            {
                case "/":
                case "/snapshot":
                    return JsonConvert.SerializeObject(snapshot);
                case "/tiles":
                    return JsonConvert.SerializeObject(new
                    {
                        success = true, snapshotId = snapshot.snapshotId,
                        count = snapshot.tileCount, tiles = snapshot.tiles, timestamp = snapshot.timestamp
                    });
                case "/tiles/count":
                    return JsonConvert.SerializeObject(new
                    {
                        success = true, snapshotId = snapshot.snapshotId,
                        count = snapshot.tileCount, timestamp = snapshot.timestamp
                    });
                case "/destinations":
                    var seen = new HashSet<string>();
                    var destinations = snapshot.tiles.FindAll(tile =>
                        tile.contentType == "Destination" &&
                        tile.buildingId != null && seen.Add(tile.buildingId));
                    return JsonConvert.SerializeObject(new
                    {
                        success = true, snapshotId = snapshot.snapshotId,
                        count = destinations.Count, destinations = destinations, timestamp = snapshot.timestamp
                    });
                case "/game/status":
                    return JsonConvert.SerializeObject(new
                    {
                        success = true, snapshotId = snapshot.snapshotId,
                        editCommandsExecuted = snapshot.editCommandsExecuted,
                        phase = snapshot.phase, gameUI = snapshot.gameUI,
                        tilemapView = snapshot.tilemapView, camera = snapshot.camera,
                        timeScaleMode = snapshot.timeScaleMode,
                        initialSeed = snapshot.initialSeed,
                        tileCount = snapshot.tileCount, score = snapshot.score,
                        clock = snapshot.clock, resources = snapshot.resources,
                        timestamp = snapshot.timestamp
                    });
                default:
                    throw new ArgumentOutOfRangeException(nameof(path));
            }
        }

        private void OnDestroy()
        {
            isRunning = false;
            httpListener?.Close();
            if (httpThread != null && httpThread.IsAlive)
                httpThread.Join(1000);

            SnapshotRequest request;
            while (requests.TryDequeue(out request))
                request.Completion.TrySetResult(new ApiResponse(503,
                    "{\"success\":false,\"error\":\"Plugin stopped\"}"));

            gridGameUI = null;
            gridTilemapView = null;
            pluginInstance = null;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(TilemapModel), "GetAllTileCoordinates")]
        private static void OnGetAllTileCoordinates(TilemapModel __instance)
        {
            if (pluginInstance != null)
                pluginInstance.tilemap = __instance;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(TilemapModel), "Reset")]
        private static void OnTilemapReset(TilemapModel __instance)
        {
            if (pluginInstance != null && pluginInstance.tilemap == __instance)
                pluginInstance.tilemap = null;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(EditTileCommand), "Execute")]
        private static void OnEditCommandExecute()
        {
            if (pluginInstance != null)
                pluginInstance.editCommandsExecuted++;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(InitCityCommand), "CreateNormalCity")]
        private static void UseFixedSimulationSeed(ref uint seed)
        {
            seed = FixedSimulationSeed;
            if (pluginInstance != null)
                pluginInstance.Logger.LogInfo("Using fixed simulation seed " + seed);
        }

        [HarmonyPrefix, HarmonyPatch(typeof(InitCityCommand), "Initialize")]
        private static void CaptureInitialSimulationSeed(ulong seed)
        {
            if (pluginInstance != null)
            {
                // snapshotId identifies captures within one game. Reset it at
                // the city initialization boundary so API restarts, UI
                // restarts, and newly started games all begin at snapshot 1.
                pluginInstance.snapshotId = 0;
                pluginInstance.initialSimulationSeed = seed;
            }
        }

        [HarmonyPrefix, HarmonyPatch(typeof(GameUIScreen), nameof(GameUIScreen.SetWorldGridActive))]
        private static void KeepWorldGridVisible(ref bool active)
        {
            if (pluginInstance != null && pluginInstance.alwaysShowWorldGrid)
                active = true;
        }
    }
}
