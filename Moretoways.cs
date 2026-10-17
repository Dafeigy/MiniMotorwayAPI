using BepInEx;
using BepInEx.Unity.Mono;
using HarmonyLib;
using Motorways;
using Motorways.Commands;
using Motorways.Models;
using Motorways.Processes;
using Motorways.Views;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
        private const string Logo =
            "\n"+
            "##::::'##::'#######::'########::'########:'########::'#######::'##:::::'##::::'###::::'##:::'##::'######::\n"+
            "###::'###:'##.... ##: ##.... ##: ##.....::... ##..::'##.... ##: ##:'##: ##:::'## ##:::. ##:'##::'##... ##:\n"+
            "####'####: ##:::: ##: ##:::: ##: ##:::::::::: ##:::: ##:::: ##: ##: ##: ##::'##:. ##:::. ####::: ##:::..::\n"+
            "## ### ##: ##:::: ##: ########:: ######:::::: ##:::: ##:::: ##: ##: ##: ##:'##:::. ##:::. ##::::. ######::\n"+
            "##. #: ##: ##:::: ##: ##.. ##::: ##...::::::: ##:::: ##:::: ##: ##: ##: ##: #########:::: ##:::::..... ##:\n"+
            "##:.:: ##: ##:::: ##: ##::. ##:: ##:::::::::: ##:::: ##:::: ##: ##: ##: ##: ##.... ##:::: ##::::'##::: ##:\n"+
            "##:::: ##:. #######:: ##:::. ##: ########:::: ##::::. #######::. ###. ###:: ##:::: ##:::: ##::::. ######::\n"+
            "..:::::..:::.......:::..:::::..::........:::::..::::::.......::::...::...:::..:::::..:::::..::::::......:::";
        private const int Port = 9000;
        private const int RequestTimeoutMs = 5000;
        private const int MaxRequestIdLength = 128;
        private const int MaxActionRecords = 4096;
        private const int MaxEventRecords = 1024;
        // Fresh normal games retain the historical default seed.
        private const uint FixedSimulationSeed = 20261014u;
        private const float NewGameLoadTimeoutSeconds = 30f;

        private readonly ConcurrentQueue<SnapshotRequest> requests = new ConcurrentQueue<SnapshotRequest>();
        private readonly Dictionary<string, SnapshotRequest> actionRequests =
            new Dictionary<string, SnapshotRequest>(StringComparer.Ordinal);
        private readonly object actionRequestsLock = new object();
        private readonly Dictionary<string, ActionRecord> actionRecords =
            new Dictionary<string, ActionRecord>(StringComparer.Ordinal);
        private readonly Queue<string> actionRecordOrder = new Queue<string>();
        private readonly Dictionary<EditTileCommand, string> pendingEditActions =
            new Dictionary<EditTileCommand, string>();
        private readonly object actionRecordsLock = new object();
        private readonly List<GameEventRecord> eventRecords = new List<GameEventRecord>();
        private readonly object eventRecordsLock = new object();
        private readonly HashSet<string> activeOvercrowdingDestinations =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> warnedOvercrowdingDestinations =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> autoPauseReasons =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly object decisionLock = new object();
        private HttpListener httpListener;
        private Thread httpThread;
        private volatile bool isRunning;
        private TilemapModel tilemap;
        private long snapshotId;
        private int editCommandsExecuted;
        private int lastScore;
        private ulong? initialSimulationSeed;
        private ulong? nextGameSeed;
        private string episodeId;
        private string terminalReason;
        private string terminalDestinationBuildingId;
        private string activeActionId;
        private long nextEventId;
        private string decisionMode = "off";
        private double minimumSimulationInterval = 60d;
        private double lastDecisionActionSimulationTime;
        private bool decisionPending;
        private bool decisionCapturePending;
        private long? decisionEventId;
        private int? decisionTick;
        private double? decisionSimulationTime;
        private List<string> decisionReasons = new List<string>();
        private long? stagedDecisionEventId;
        private int? stagedDecisionTick;
        private double? stagedDecisionSimulationTime;
        private List<string> stagedDecisionReasons = new List<string>();
        private readonly HashSet<string> pendingCriticalReasons =
            new HashSet<string>(StringComparer.Ordinal);
        private long? pendingCriticalEventId;
        private GameSnapshot decisionSnapshot;
        private NewGameLaunch newGameLaunch;
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
            public readonly string RequestId;
            public readonly string ActionId;
            public readonly string EpisodeId;
            private readonly long deadlineTimestamp;
            // 0 = queued, 1 = running, 2 = complete, 3 = expired.
            private int state;
            public readonly TaskCompletionSource<ApiResponse> Completion =
                new TaskCompletionSource<ApiResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

            public SnapshotRequest(string path, string body, string requestId,
                string actionId = null, string episodeId = null)
            {
                Path = path;
                Body = body;
                RequestId = requestId;
                ActionId = actionId;
                EpisodeId = episodeId;
                deadlineTimestamp = Stopwatch.GetTimestamp() +
                    Stopwatch.Frequency * RequestTimeoutMs / 1000;
            }

            public string State
            {
                get
                {
                    switch (Volatile.Read(ref state))
                    {
                        case 0: return "queued";
                        case 1: return "running";
                        case 2: return "complete";
                        default: return "expired";
                    }
                }
            }

            public bool Matches(string path, string body)
            {
                return Path == path && string.Equals(Body, body, StringComparison.Ordinal);
            }

            public bool TryBegin()
            {
                if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
                    return false;

                if (Stopwatch.GetTimestamp() < deadlineTimestamp)
                    return true;

                Interlocked.Exchange(ref state, 3);
                Completion.TrySetResult(ExpiredResponse());
                return false;
            }

            public bool TryExpire()
            {
                if (Interlocked.CompareExchange(ref state, 3, 0) != 0)
                    return false;

                Completion.TrySetResult(ExpiredResponse());
                return true;
            }

            public void Complete(ApiResponse response)
            {
                Completion.TrySetResult(response);
                Volatile.Write(ref state, 2);
            }

            private ApiResponse ExpiredResponse()
            {
                return new ApiResponse(503, JsonConvert.SerializeObject(new
                {
                    success = false,
                    requestId = RequestId,
                    actionId = ActionId,
                    episodeId = EpisodeId,
                    state = "expired",
                    error = "Game main thread did not start this request before its deadline"
                }));
            }
        }

        private sealed class ActionRecord
        {
            public bool success = true;
            public string actionId;
            public string requestId;
            public string episodeId;
            public string resultingEpisodeId;
            public string path;
            public string state;
            public string resultCode;
            public string error;
            public int? responseStatusCode;
            public DateTime queuedAt;
            public DateTime? scheduledAt;
            public DateTime? executedAt;
            public int? scheduledTick;
            public double? scheduledSimulationTime;
            public int? executedTick;
            public double? executedSimulationTime;
            public int scheduledCommands;
            public int executedCommands;
            public int failedCommands;
            [JsonIgnore]
            public bool submissionComplete;
            [JsonIgnore]
            public bool awaitingEpisodeStart;
        }

        private sealed class GameEventRecord
        {
            public long eventId;
            public string episodeId;
            public int? simulationTick;
            public double? simulationTime;
            public DateTime timestamp;
            public List<string> reasons = new List<string>();
            public List<object> details = new List<object>();
        }

        private sealed class DecisionConfigRequest
        {
            public string mode { get; set; }
            public double? minimumSimulationInterval { get; set; }
            public List<string> autoPauseReasons { get; set; }
        }

        private sealed class LegalActionRequest
        {
            public string type { get; set; }
            public int? x { get; set; }
            public int? y { get; set; }
            public string direction { get; set; }
            public string clearType { get; set; }
            public int? startX { get; set; }
            public int? startY { get; set; }
            public string startDirection { get; set; }
            public int? endX { get; set; }
            public int? endY { get; set; }
            public string endDirection { get; set; }
            public int? optionIndex { get; set; }
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
            public string type { get; set; }
        }

        private sealed class MotorwayRequest
        {
            public int? startX { get; set; }
            public int? startY { get; set; }
            public string startDirection { get; set; }
            public int? endX { get; set; }
            public int? endY { get; set; }
            public string endDirection { get; set; }
        }

        private sealed class UpgradeChoiceRequest
        {
            public int? optionIndex { get; set; }
        }

        private sealed class NewGameRequest
        {
            public string map { get; set; }
            public uint? seed { get; set; }
            public bool startPaused { get; set; }
        }

        private sealed class RestartGameRequest
        {
            public uint? seed { get; set; }
        }

        private sealed class NewGameLaunch
        {
            public GameStarter Starter;
            public ScreenStack ScreenStack;
            public Factory.IScope AppScope;
            public string Map;
            public uint Seed;
            public string SourceScreen;
            public float StartedAt;
            public string State;
            public string Error;
        }

        private sealed class ApiResponse
        {
            public readonly int StatusCode;
            public readonly string Json;
            public readonly double? SnapshotCaptureMs;
            public readonly double? SnapshotSerializeMs;

            public ApiResponse(int statusCode, string json,
                double? snapshotCaptureMs = null, double? snapshotSerializeMs = null)
            {
                StatusCode = statusCode;
                Json = json;
                SnapshotCaptureMs = snapshotCaptureMs;
                SnapshotSerializeMs = snapshotSerializeMs;
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
            public object terrain;
            public object traffic;
        }

        private sealed class GameSnapshot
        {
            public bool success = true;
            public long snapshotId;
            public string episodeId;
            public int? simulationTick;
            public double? simulationTime;
            public string terminalReason;
            public string terminalDestinationBuildingId;
            public int editCommandsExecuted;
            public DateTime timestamp;
            public string phase;
            public bool gameUI;
            public bool tilemapView;
            public bool camera;
            public string timeScaleMode;
            public ulong? initialSeed;
            public string map;
            public object mapBounds;
            public int? score;
            public object clock;
            public object resources;
            public int vehicleCount;
            public List<VehicleSnapshot> vehicles = new List<VehicleSnapshot>();
            public int motorwayCount;
            public List<MotorwaySnapshot> motorways = new List<MotorwaySnapshot>();
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

        private sealed class MotorwaySnapshot
        {
            public int motorwayId;
            public int number;
            public string state;
            public bool hasConsumedUpgrade;
            public object startCoordinates;
            public string startDirection;
            public object endCoordinates;
            public string endDirection;
        }

        private void Start()
        {
            Logger.LogMessage(Logo);
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
            string method = context.Request.HttpMethod;
            string path = context.Request.Url.AbsolutePath;
            try
            {
                AddCorsHeaders(response);
                if (method == "OPTIONS")
                {
                    SendJson(response, 200, "{}");
                    return;
                }

                bool isQueryPost = path == "/actions/legal";
                bool isAction = path == "/actions/road" || path == "/actions/clear" ||
                    path == "/actions/traffic-light" || path == "/actions/roundabout" ||
                    path == "/actions/motorway" || path == "/actions/upgrade" ||
                    path == "/actions/noop" ||
                    path == "/decision/config" ||
                    path == "/game/new" || path == "/game/restart" || path == "/game/pause" ||
                    path == "/game/resume";
                if (method != (isAction || isQueryPost ? "POST" : "GET"))
                {
                    SendJson(response, 405, "{\"error\":\"Method Not Allowed\"}");
                    return;
                }

                if (path != "/" && path != "/snapshot" && path != "/requests" && path != "/tiles" &&
                    path != "/tiles/count" && path != "/destinations" &&
                    path != "/game/status" && path != "/game/maps" &&
                    path != "/game/upgrades" && path != "/actions" &&
                    path != "/events" && path != "/decision/status" &&
                    !isAction && !isQueryPost)
                {
                    SendJson(response, 404, "{\"error\":\"Not Found\"}");
                    return;
                }

                if (path == "/requests")
                {
                    SendRequestStatus(context.Request, response);
                    return;
                }
                if (path == "/actions")
                {
                    SendActionStatus(context.Request, response);
                    return;
                }
                if (path == "/events")
                {
                    SendEvents(context.Request, response);
                    return;
                }
                if (path == "/decision/status")
                {
                    SendDecisionStatus(response);
                    return;
                }

                string requestId = null;
                if (isAction)
                {
                    requestId = context.Request.Headers["X-Request-Id"];
                    if (string.IsNullOrEmpty(requestId))
                        requestId = Guid.NewGuid().ToString("N");
                    else if (!IsValidRequestId(requestId))
                    {
                        SendJson(response, 400,
                            "{\"success\":false,\"error\":\"Invalid X-Request-Id\"}");
                        return;
                    }
                    response.Headers["X-Request-Id"] = requestId;
                }

                string body = null;
                if (path == "/actions/road" || path == "/actions/clear" ||
                    path == "/actions/traffic-light" || path == "/actions/roundabout" ||
                    path == "/actions/motorway" || path == "/actions/upgrade" ||
                    path == "/decision/config" ||
                    path == "/game/new" || path == "/game/restart" || isQueryPost)
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

                SnapshotRequest request;
                bool enqueue = true;
                bool requestIdConflict = false;
                if (isAction)
                {
                    lock (actionRequestsLock)
                    {
                        if (actionRequests.TryGetValue(requestId, out request))
                        {
                            if (!request.Matches(path, body))
                                requestIdConflict = true;
                            enqueue = false;
                        }
                        else
                        {
                            string actionId = Guid.NewGuid().ToString("N");
                            request = new SnapshotRequest(path, body, requestId,
                                actionId, episodeId);
                            actionRequests.Add(requestId, request);
                            RegisterAction(request);
                        }
                    }
                    if (requestIdConflict)
                    {
                        SendJson(response, 409,
                            "{\"success\":false,\"error\":\"Request ID was used for a different action\"}");
                        return;
                    }
                }
                else
                {
                    request = new SnapshotRequest(path, body, null);
                }

                if (enqueue)
                    requests.Enqueue(request);
                if (!request.Completion.Task.Wait(RequestTimeoutMs))
                {
                    // A queued request can be cancelled safely. A request that
                    // already started may still finish, so expose its identity
                    // instead of reporting that the action was not executed.
                    if (request.TryExpire() || request.Completion.Task.IsCompleted)
                    {
                        ExpireAction(request.ActionId,
                            "Game main thread did not start this action before its deadline");
                        ApiResponse timedResult = request.Completion.Task.Result;
                        SendApiResponse(response, timedResult);
                        return;
                    }

                    SendJson(response, isAction ? 202 : 503, isAction
                        ? JsonConvert.SerializeObject(new
                        {
                            success = true,
                            requestId = requestId,
                            actionId = request.ActionId,
                            state = request.State
                        })
                        : "{\"error\":\"Game main thread did not respond\"}");
                    return;
                }

                ApiResponse result = request.Completion.Task.Result;
                SendApiResponse(response, result);
            }
            catch (ObjectDisposedException)
            {
                Logger.LogDebug("HTTP client disconnected before response: " + method + " " + path);
            }
            catch (HttpListenerException exception)
            {
                Logger.LogDebug("HTTP connection ended before response: " + method + " " + path +
                    " (" + exception.ErrorCode + ")");
            }
            catch (IOException exception)
            {
                Logger.LogDebug("HTTP stream ended before response: " + method + " " + path +
                    " (" + exception.Message + ")");
            }
            catch (Exception exception)
            {
                Logger.LogError("HTTP request failed for " + method + " " + path + ": " + exception);
                TrySendJson(response, 500, "{\"error\":\"Internal Server Error\"}");
            }
            finally
            {
                try
                {
                    response.Close();
                }
                catch (ObjectDisposedException) { }
            }
        }

        private static void AddCorsHeaders(HttpListenerResponse response)
        {
            response.Headers["Access-Control-Allow-Origin"] = "*";
            response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            response.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Request-Id";
            response.Headers["Access-Control-Expose-Headers"] =
                "X-Request-Id, X-Snapshot-Capture-Ms, X-Snapshot-Serialize-Ms";
            response.Headers["Access-Control-Max-Age"] = "600";
        }

        private static bool IsValidRequestId(string requestId)
        {
            if (requestId.Length == 0 || requestId.Length > MaxRequestIdLength)
                return false;

            foreach (char character in requestId)
            {
                if ((character >= 'a' && character <= 'z') ||
                    (character >= 'A' && character <= 'Z') ||
                    (character >= '0' && character <= '9') ||
                    character == '-' || character == '_' || character == '.' || character == ':')
                    continue;
                return false;
            }
            return true;
        }

        private void SendRequestStatus(HttpListenerRequest httpRequest, HttpListenerResponse response)
        {
            string requestId = httpRequest.QueryString["id"];
            if (string.IsNullOrEmpty(requestId) || !IsValidRequestId(requestId))
            {
                SendJson(response, 400,
                    "{\"success\":false,\"error\":\"Expected a valid id query parameter\"}");
                return;
            }

            SnapshotRequest request;
            lock (actionRequestsLock)
                actionRequests.TryGetValue(requestId, out request);
            if (request == null)
            {
                SendJson(response, 404,
                    "{\"success\":false,\"error\":\"Unknown request ID\"}");
                return;
            }

            ApiResponse result = request.Completion.Task.IsCompleted
                ? request.Completion.Task.Result : null;
            SendJson(response, 200, JsonConvert.SerializeObject(new
            {
                success = true,
                requestId = requestId,
                actionId = request.ActionId,
                actionState = GetActionState(request.ActionId),
                state = request.State,
                responseStatusCode = result == null ? (int?)null : result.StatusCode,
                response = result == null ? null : JsonConvert.DeserializeObject(result.Json)
            }));
        }

        private void SendActionStatus(HttpListenerRequest httpRequest,
            HttpListenerResponse response)
        {
            string actionId = httpRequest.QueryString["id"];
            if (string.IsNullOrEmpty(actionId) || !IsValidRequestId(actionId))
            {
                SendJson(response, 400,
                    "{\"success\":false,\"error\":\"Expected a valid id query parameter\"}");
                return;
            }

            string json;
            lock (actionRecordsLock)
            {
                ActionRecord record;
                if (!actionRecords.TryGetValue(actionId, out record))
                {
                    SendJson(response, 404,
                        "{\"success\":false,\"error\":\"Unknown action ID\"}");
                    return;
                }
                json = JsonConvert.SerializeObject(record);
            }
            SendJson(response, 200, json);
        }

        private string GetActionState(string actionId)
        {
            if (actionId == null)
                return null;
            lock (actionRecordsLock)
            {
                ActionRecord record;
                return actionRecords.TryGetValue(actionId, out record) ? record.state : null;
            }
        }

        private void SendEvents(HttpListenerRequest request, HttpListenerResponse response)
        {
            long after = 0;
            string rawAfter = request.QueryString["after"];
            if (!string.IsNullOrEmpty(rawAfter) &&
                (!long.TryParse(rawAfter, NumberStyles.None, CultureInfo.InvariantCulture, out after) ||
                 after < 0))
            {
                SendJson(response, 400,
                    "{\"success\":false,\"error\":\"after must be a nonnegative integer\"}");
                return;
            }

            string json;
            lock (eventRecordsLock)
            {
                long oldestEventId = eventRecords.Count == 0 ? nextEventId + 1 :
                    eventRecords[0].eventId;
                var events = new List<GameEventRecord>();
                foreach (GameEventRecord gameEvent in eventRecords)
                {
                    if (gameEvent.eventId > after)
                        events.Add(gameEvent);
                }
                json = JsonConvert.SerializeObject(new
                {
                    success = true,
                    episodeId = episodeId,
                    after = after,
                    oldestEventId = oldestEventId,
                    latestEventId = nextEventId,
                    gap = eventRecords.Count > 0 && after < oldestEventId - 1,
                    events = events
                });
            }
            SendJson(response, 200, json);
        }

        private void SendDecisionStatus(HttpListenerResponse response)
        {
            string json;
            lock (decisionLock)
                json = SerializeDecisionStatus();
            SendJson(response, 200, json);
        }

        private string SerializeDecisionStatus()
        {
            var reasons = new List<string>(decisionReasons);
            var pauseReasons = new List<string>(autoPauseReasons);
            pauseReasons.Sort(StringComparer.Ordinal);
            return JsonConvert.SerializeObject(new
            {
                success = true,
                episodeId = episodeId,
                mode = decisionMode,
                minimumSimulationInterval = minimumSimulationInterval,
                autoPauseReasons = pauseReasons,
                pending = decisionPending,
                eventId = decisionEventId,
                simulationTick = decisionTick,
                simulationTime = decisionSimulationTime,
                reasons = reasons,
                snapshotId = decisionSnapshot == null ? (long?)null : decisionSnapshot.snapshotId
            });
        }

        private static bool TrySendJson(HttpListenerResponse response, int statusCode, string json)
        {
            try
            {
                SendJson(response, statusCode, json);
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (HttpListenerException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        private static void SendApiResponse(HttpListenerResponse response, ApiResponse result)
        {
            if (result.SnapshotCaptureMs.HasValue)
                response.Headers["X-Snapshot-Capture-Ms"] =
                    result.SnapshotCaptureMs.Value.ToString("F3", CultureInfo.InvariantCulture);
            if (result.SnapshotSerializeMs.HasValue)
                response.Headers["X-Snapshot-Serialize-Ms"] =
                    result.SnapshotSerializeMs.Value.ToString("F3", CultureInfo.InvariantCulture);
            SendJson(response, result.StatusCode, result.Json);
        }

        private static void SendJson(HttpListenerResponse response, int statusCode, string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentEncoding = Encoding.UTF8;
            response.ContentLength64 = bytes.Length;
            response.KeepAlive = false;
            response.SendChunked = false;
            response.Headers["Cache-Control"] = "no-store";

            // Mono's HttpListener does not reliably flush a response body when
            // game control changes screens or pauses the simulation between an
            // OutputStream.Write call and the outer finally block. This overload
            // writes the complete body synchronously and closes the response
            // before returning to the listener loop.
            response.Close(bytes, true);
        }

        private void RegisterAction(SnapshotRequest request)
        {
            var record = new ActionRecord
            {
                actionId = request.ActionId,
                requestId = request.RequestId,
                episodeId = request.EpisodeId,
                path = request.Path,
                state = "queued",
                queuedAt = DateTime.UtcNow
            };
            lock (actionRecordsLock)
            {
                actionRecords.Add(record.actionId, record);
                actionRecordOrder.Enqueue(record.actionId);
                while (actionRecordOrder.Count > MaxActionRecords)
                {
                    string expiredId = actionRecordOrder.Dequeue();
                    ActionRecord expiredRecord;
                    if (actionRecords.TryGetValue(expiredId, out expiredRecord))
                    {
                        actionRecords.Remove(expiredId);
                        actionRequests.Remove(expiredRecord.requestId);
                    }
                }
            }
        }

        private void ExpireAction(string actionId, string error)
        {
            if (actionId == null)
                return;
            lock (actionRecordsLock)
            {
                ActionRecord record;
                if (!actionRecords.TryGetValue(actionId, out record) ||
                    record.state == "executed" || record.state == "rejected" ||
                    record.state == "expired")
                    return;
                record.state = "expired";
                record.resultCode = "Expired";
                record.error = error;
            }
        }

        private bool RejectActionFromPreviousEpisode(SnapshotRequest request)
        {
            if (request.ActionId == null || request.Path == "/game/new" ||
                request.Path == "/game/restart" || request.EpisodeId == episodeId)
                return false;

            ExpireAction(request.ActionId,
                "Action belongs to a previous episode and was not executed");
            request.Complete(AttachActionMetadata(request, new ApiResponse(409,
                "{\"success\":false,\"resultCode\":\"EpisodeChanged\"," +
                "\"error\":\"Action belongs to a previous episode\"}")));
            return true;
        }

        private ApiResponse CompleteAction(SnapshotRequest request, ApiResponse response)
        {
            if (request.ActionId == null)
                return response;

            JObject payload = null;
            try { payload = JObject.Parse(response.Json); }
            catch (JsonException) { }

            string resultCode = payload == null ? null : (string)payload["resultCode"];
            string error = payload == null ? null : (string)payload["error"];
            int? tick;
            double? simulationTime;
            GetCurrentSimulationPosition(out tick, out simulationTime);

            lock (actionRecordsLock)
            {
                ActionRecord record;
                if (actionRecords.TryGetValue(request.ActionId, out record))
                {
                    record.responseStatusCode = response.StatusCode;
                    record.resultCode = resultCode ??
                        (response.StatusCode >= 400 ? "Rejected" : "Executed");
                    record.error = error;
                    record.submissionComplete = true;
                    record.awaitingEpisodeStart = response.StatusCode == 202 &&
                        (request.Path == "/game/new" || request.Path == "/game/restart");

                    if (record.scheduledCommands > 0 || record.awaitingEpisodeStart)
                    {
                        record.state = "scheduled";
                        record.scheduledAt = DateTime.UtcNow;
                        record.scheduledTick = tick;
                        record.scheduledSimulationTime = simulationTime;
                    }
                    else if (response.StatusCode >= 400)
                    {
                        record.state = "rejected";
                    }
                    else
                    {
                        record.state = "executed";
                        record.executedAt = DateTime.UtcNow;
                        record.executedTick = tick;
                        record.executedSimulationTime = simulationTime;
                    }

                    if (record.scheduledCommands > 0 &&
                        record.executedCommands >= record.scheduledCommands)
                        MarkActionExecuted(record, tick, simulationTime);
                }
            }
            return AttachActionMetadata(request, response);
        }

        private ApiResponse AttachActionMetadata(SnapshotRequest request, ApiResponse response)
        {
            if (request.ActionId == null)
                return response;
            try
            {
                JObject payload = JObject.Parse(response.Json);
                payload["requestId"] = request.RequestId;
                payload["actionId"] = request.ActionId;
                payload["episodeId"] = request.EpisodeId == null ?
                    JValue.CreateNull() : new JValue(request.EpisodeId);
                return new ApiResponse(response.StatusCode,
                    JsonConvert.SerializeObject(payload), response.SnapshotCaptureMs,
                    response.SnapshotSerializeMs);
            }
            catch (JsonException)
            {
                return response;
            }
        }

        private void MarkActionExecuted(ActionRecord record, int? tick,
            double? simulationTime)
        {
            record.state = "executed";
            record.executedAt = DateTime.UtcNow;
            record.executedTick = tick;
            record.executedSimulationTime = simulationTime;
            if (simulationTime.HasValue && record.path != null &&
                record.path.StartsWith("/actions/", StringComparison.Ordinal))
            {
                lock (decisionLock)
                    lastDecisionActionSimulationTime = simulationTime.Value;
            }
            InvalidateDecisionSnapshot();
        }

        private void InvalidateDecisionSnapshot()
        {
            lock (decisionLock)
                decisionSnapshot = null;
        }

        private GameSnapshot CaptureOrReuseSnapshot()
        {
            lock (decisionLock)
            {
                if (decisionPending && decisionSnapshot != null)
                    return decisionSnapshot;
            }
            GameSnapshot snapshot = CaptureSnapshot();
            lock (decisionLock)
            {
                if (decisionPending && decisionSnapshot == null)
                    decisionSnapshot = snapshot;
            }
            return snapshot;
        }

        private void GetCurrentSimulationPosition(out int? tick, out double? simulationTime)
        {
            tick = null;
            simulationTime = null;
            var gameUI = UnityEngine.Object.FindObjectOfType<GameUIScreen>();
            var simulation = gameUI == null ? null :
                Traverse.Create(gameUI).Field("_simulation").GetValue<Server.ISimulation>();
            CaptureSimulationPosition(simulation, out tick, out simulationTime);
        }

        private static void CaptureSimulationPosition(Server.ISimulation simulation,
            out int? tick, out double? simulationTime)
        {
            tick = null;
            simulationTime = null;
            if (simulation == null)
                return;
            ClockModel clock = simulation.GetModel<ClockModel>();
            if (clock == null || clock.Clock == null)
                return;
            tick = clock.Clock.FrameCount;
            simulationTime = (double)clock.Clock.Time;
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.M) &&
                (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
            {
                ToggleWorldGridMode();
            }

            AdvanceNewGameLaunch();
            AdvancePendingDecisionBoundary();

            SnapshotRequest request;
            // A paused game can leave several requests in the queue past their
            // deadlines. Skip all expired entries in the same frame so they do
            // not delay the next live request by one frame each.
            while (true)
            {
                if (!requests.TryDequeue(out request))
                    return;
                if (request.TryBegin())
                    break;
                ExpireAction(request.ActionId,
                    "Game main thread did not start this action before its deadline");
            }

            if (RejectActionFromPreviousEpisode(request))
                return;

            activeActionId = request.ActionId;
            if (request.ActionId != null && request.Path != "/decision/config")
                InvalidateDecisionSnapshot();
            try
            {
                // Game objects are copied on Unity's main thread. The HTTP thread
                // receives JSON only and never accesses a live game object.
                ApiResponse result;
                if (request.Path == "/actions/road")
                    result = AddRoad(request.Body);
                else if (request.Path == "/actions/clear")
                    result = ClearRoads(request.Body);
                else if (request.Path == "/actions/traffic-light")
                    result = AddTrafficLight(request.Body);
                else if (request.Path == "/actions/roundabout")
                    result = AddRoundabout(request.Body);
                else if (request.Path == "/actions/motorway")
                    result = AddMotorway(request.Body);
                else if (request.Path == "/actions/upgrade")
                    result = SelectUpgrade(request.Body);
                else if (request.Path == "/actions/noop")
                    result = NoOp();
                else if (request.Path == "/actions/legal")
                    result = CheckLegalAction(request.Body);
                else if (request.Path == "/decision/config")
                    result = ConfigureDecisionPolicy(request.Body);
                else if (request.Path == "/game/restart")
                    result = RestartGame(request.Body);
                else if (request.Path == "/game/new")
                    result = StartNewGame(request.Body);
                else if (request.Path == "/game/pause")
                    result = SetGamePaused(true);
                else if (request.Path == "/game/resume")
                    result = SetGamePaused(false);
                else if (request.Path == "/game/maps")
                    result = GetGameMaps();
                else if (request.Path == "/game/upgrades")
                    result = GetUpgradeChoices();
                else
                {
                    long captureStarted = Stopwatch.GetTimestamp();
                    GameSnapshot snapshot = CaptureOrReuseSnapshot();
                    long serializeStarted = Stopwatch.GetTimestamp();
                    string json = SerializeResponse(snapshot, request.Path);
                    long finished = Stopwatch.GetTimestamp();
                    result = new ApiResponse(200, json,
                        (serializeStarted - captureStarted) * 1000.0 / Stopwatch.Frequency,
                        (finished - serializeStarted) * 1000.0 / Stopwatch.Frequency);
                }
                request.Complete(CompleteAction(request, result));
            }
            catch (Exception exception)
            {
                Logger.LogError("Snapshot failed: " + exception);
                request.Complete(CompleteAction(request, new ApiResponse(500,
                    "{\"success\":false,\"error\":\"Request failed\"}")));
            }
            finally
            {
                activeActionId = null;
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

            TileEditor.ClearTileOfType clearType = TileEditor.ClearTileOfType.Roads;
            if (input.type != null &&
                (!Enum.TryParse(input.type, true, out clearType) ||
                 !Enum.IsDefined(typeof(TileEditor.ClearTileOfType), clearType)))
                return new ApiResponse(400,
                    "{\"success\":false,\"error\":\"Invalid clear type\"}");

            return ApplyTileEdit((editor, view) => editor.ClearTileExplicit(
                view, new Vector2Int(input.x.Value, input.y.Value),
                clearType,
                Tile.TileChangePermissions.RespectPermanence));
        }

        private ApiResponse AddTrafficLight(string body)
        {
            ClearRequest input;
            try { input = JsonConvert.DeserializeObject<ClearRequest>(body); }
            catch (JsonException)
            {
                return new ApiResponse(400, "{\"success\":false,\"error\":\"Invalid JSON\"}");
            }
            if (input == null || !input.x.HasValue || !input.y.HasValue)
                return new ApiResponse(400,
                    "{\"success\":false,\"error\":\"Expected x and y\"}");

            return ApplyTileEdit((editor, view) => editor.AddTrafficLight(
                view, new Vector2Int(input.x.Value, input.y.Value)));
        }

        private ApiResponse AddRoundabout(string body)
        {
            ClearRequest input;
            try { input = JsonConvert.DeserializeObject<ClearRequest>(body); }
            catch (JsonException)
            {
                return new ApiResponse(400, "{\"success\":false,\"error\":\"Invalid JSON\"}");
            }
            if (input == null || !input.x.HasValue || !input.y.HasValue)
                return new ApiResponse(400,
                    "{\"success\":false,\"error\":\"Expected x and y\"}");

            return ApplyTileEdit((editor, view) => editor.AddRoundabout(
                view, new Vector2Int(input.x.Value, input.y.Value)));
        }

        private ApiResponse AddMotorway(string body)
        {
            MotorwayRequest input;
            try { input = JsonConvert.DeserializeObject<MotorwayRequest>(body); }
            catch (JsonException)
            {
                return new ApiResponse(400, "{\"success\":false,\"error\":\"Invalid JSON\"}");
            }

            TileDirection startDirection;
            TileDirection endDirection;
            if (input == null || !input.startX.HasValue || !input.startY.HasValue ||
                !input.endX.HasValue || !input.endY.HasValue ||
                !Enum.TryParse(input.startDirection, true, out startDirection) ||
                !Enum.TryParse(input.endDirection, true, out endDirection) ||
                (int)startDirection < 0 || (int)startDirection > 7 ||
                (int)endDirection < 0 || (int)endDirection > 7)
            {
                return new ApiResponse(400,
                    "{\"success\":false,\"error\":\"Expected two coordinates and compass directions\"}");
            }

            var start = new Vector2Int(input.startX.Value, input.startY.Value);
            var end = new Vector2Int(input.endX.Value, input.endY.Value);
            if (start == end)
                return new ApiResponse(400,
                    "{\"success\":false,\"error\":\"Motorway endpoints must differ\"}");

            City city = null;
            int motorwayId = -1;
            int motorwayNumber = 0;
            // The native interaction first schedules an unbuilt anchor. That
            // edit consumes the motorway asset; the finished edit marks the
            // new motorway as already paid for. Skipping the anchor would
            // leave the asset available after construction.
            ApiResponse anchorResponse = ApplyTileEdit((editor, view) =>
            {
                var scope = Traverse.Create(view).Field("_scope").GetValue<Factory.IScope>();
                city = scope == null ? null : scope.Get<City>();
                if (city == null)
                    throw new InvalidOperationException("City is unavailable");
                motorwayId = city.NextMotorwayId;
                motorwayNumber = view.GetLowestAvailableMotorwayNumber();
                var candidate = editor.AddMotorway(view, motorwayId, motorwayNumber,
                    start, startDirection, end, endDirection, -1);
                if (!candidate.IsSuccessful)
                    return candidate;
                if (candidate.edit != null)
                    scope.Release(candidate.edit);
                return editor.AddUnbuiltMotorway(view, motorwayId, motorwayNumber, start);
            }, () =>
            {
                if (city.GetNextMotorwayIdAndIncrement() != motorwayId)
                    throw new InvalidOperationException("Motorway ID changed before applying the edit");
            });
            if (anchorResponse.StatusCode != 202)
                return anchorResponse;

            return ApplyTileEdit((editor, view) => editor.AddMotorway(view,
                motorwayId, motorwayNumber, start, startDirection, end, endDirection, -1));
        }

        private static ApiResponse NoOp()
        {
            if (UnityEngine.Object.FindObjectOfType<GameUIScreen>() == null &&
                UnityEngine.Object.FindObjectOfType<GameUpgradeScreen>() == null)
                return new ApiResponse(409,
                    "{\"success\":false,\"error\":\"No active game\"}");
            return new ApiResponse(200,
                "{\"success\":true,\"scheduled\":false,\"resultCode\":\"NoOp\"}");
        }

        private ApiResponse ConfigureDecisionPolicy(string body)
        {
            DecisionConfigRequest input;
            try
            {
                JObject payload = JObject.Parse(body);
                JToken value;
                if (payload.TryGetValue("mode", out value) && value.Type != JTokenType.String ||
                    payload.TryGetValue("minimumSimulationInterval", out value) &&
                    value.Type != JTokenType.Integer && value.Type != JTokenType.Float ||
                    payload.TryGetValue("autoPauseReasons", out value) &&
                    value.Type != JTokenType.Array)
                    return new ApiResponse(400,
                        "{\"success\":false,\"error\":\"Invalid decision configuration types\"}");
                input = payload.ToObject<DecisionConfigRequest>();
            }
            catch (JsonException)
            {
                return new ApiResponse(400, "{\"success\":false,\"error\":\"Invalid JSON\"}");
            }
            if (input == null)
                return new ApiResponse(400,
                    "{\"success\":false,\"error\":\"Expected decision configuration\"}");
            if (input.mode != null && input.mode != "off" && input.mode != "agent" &&
                input.mode != "rl")
                return new ApiResponse(400,
                    "{\"success\":false,\"error\":\"mode must be off, agent or rl\"}");
            if (input.minimumSimulationInterval.HasValue &&
                (double.IsNaN(input.minimumSimulationInterval.Value) ||
                 double.IsInfinity(input.minimumSimulationInterval.Value) ||
                 input.minimumSimulationInterval.Value <= 0d))
                return new ApiResponse(400,
                    "{\"success\":false,\"error\":\"minimumSimulationInterval must be positive\"}");
            if (input.autoPauseReasons != null)
            {
                foreach (string reason in input.autoPauseReasons)
                {
                    if (!IsKnownEventReason(reason))
                        return new ApiResponse(400, JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Unknown auto-pause reason",
                            reason = reason
                        }));
                }
            }

            string json;
            lock (decisionLock)
            {
                if (input.mode != null)
                    decisionMode = input.mode;
                if (input.minimumSimulationInterval.HasValue)
                    minimumSimulationInterval = input.minimumSimulationInterval.Value;
                if (input.autoPauseReasons != null)
                {
                    autoPauseReasons.Clear();
                    foreach (string reason in input.autoPauseReasons)
                        autoPauseReasons.Add(reason);
                }
                if (decisionMode == "off")
                {
                    decisionPending = false;
                    decisionCapturePending = false;
                    decisionEventId = null;
                    decisionTick = null;
                    decisionSimulationTime = null;
                    stagedDecisionEventId = null;
                    stagedDecisionTick = null;
                    stagedDecisionSimulationTime = null;
                    stagedDecisionReasons.Clear();
                    decisionReasons.Clear();
                    pendingCriticalReasons.Clear();
                    pendingCriticalEventId = null;
                    decisionSnapshot = null;
                }
                json = SerializeDecisionStatus();
            }
            return new ApiResponse(200, json);
        }

        private ApiResponse CheckLegalAction(string body)
        {
            LegalActionRequest input;
            try { input = JsonConvert.DeserializeObject<LegalActionRequest>(body); }
            catch (JsonException)
            {
                return new ApiResponse(400, "{\"success\":false,\"error\":\"Invalid JSON\"}");
            }
            if (input == null || string.IsNullOrEmpty(input.type))
                return new ApiResponse(400,
                    "{\"success\":false,\"error\":\"Expected action type\"}");

            string type = input.type.ToLowerInvariant();
            if (type == "noop")
            {
                bool active = UnityEngine.Object.FindObjectOfType<GameUIScreen>() != null ||
                    UnityEngine.Object.FindObjectOfType<GameUpgradeScreen>() != null;
                return LegalActionResponse(type, active,
                    active ? "NoOp" : "NoActiveGame", null);
            }
            if (type == "upgrade")
            {
                if (!input.optionIndex.HasValue || input.optionIndex.Value < 0)
                    return new ApiResponse(400,
                        "{\"success\":false,\"error\":\"Expected nonnegative optionIndex\"}");
                GameUpgradeScreen screen;
                UpgradeDatabaseModel database;
                int[] mapping;
                bool available = TryGetUpgradeState(out screen, out database, out mapping) &&
                    database.pendingUpgradeChoices.Count > 0 &&
                    UpgradeScreenIsReady(screen, mapping);
                bool legal = available &&
                    input.optionIndex.Value < database.pendingUpgradeChoices[0].choices.Count &&
                    Array.IndexOf(mapping, input.optionIndex.Value) >= 0;
                return LegalActionResponse(type, legal,
                    legal ? "Legal" : available ? "UnavailableOption" : "UpgradeNotReady", null);
            }

            var gameUI = UnityEngine.Object.FindObjectOfType<GameUIScreen>();
            var tilemapView = gameUI == null ? null :
                Traverse.Create(gameUI).Field("_tilemapView").GetValue<TilemapView>();
            var scope = tilemapView == null ? null :
                Traverse.Create(tilemapView).Field("_scope").GetValue<Factory.IScope>();
            TileEditor editor = scope == null ? null : scope.Get<TileEditor>();
            if (gameUI == null || tilemapView == null || scope == null || editor == null)
                return new ApiResponse(409,
                    "{\"success\":false,\"error\":\"No active game action services\"}");

            TileEditResult result;
            if (type == "road")
            {
                TileDirection direction;
                if (!input.x.HasValue || !input.y.HasValue ||
                    !TryParseDirection(input.direction, out direction))
                    return new ApiResponse(400,
                        "{\"success\":false,\"error\":\"Expected x, y and direction\"}");
                result = editor.AddRoad(tilemapView,
                    new Vector2Int(input.x.Value, input.y.Value), direction);
            }
            else if (type == "clear")
            {
                if (!input.x.HasValue || !input.y.HasValue)
                    return new ApiResponse(400,
                        "{\"success\":false,\"error\":\"Expected x and y\"}");
                TileEditor.ClearTileOfType clearType = TileEditor.ClearTileOfType.Roads;
                if (input.clearType != null &&
                    (!Enum.TryParse(input.clearType, true, out clearType) ||
                     !Enum.IsDefined(typeof(TileEditor.ClearTileOfType), clearType)))
                    return new ApiResponse(400,
                        "{\"success\":false,\"error\":\"Invalid clearType\"}");
                result = editor.ClearTileExplicit(tilemapView,
                    new Vector2Int(input.x.Value, input.y.Value), clearType,
                    Tile.TileChangePermissions.RespectPermanence);
            }
            else if (type == "traffic-light" || type == "roundabout")
            {
                if (!input.x.HasValue || !input.y.HasValue)
                    return new ApiResponse(400,
                        "{\"success\":false,\"error\":\"Expected x and y\"}");
                Vector2Int coordinates = new Vector2Int(input.x.Value, input.y.Value);
                result = type == "traffic-light" ?
                    editor.AddTrafficLight(tilemapView, coordinates) :
                    editor.AddRoundabout(tilemapView, coordinates);
            }
            else if (type == "motorway")
            {
                TileDirection startDirection;
                TileDirection endDirection;
                if (!input.startX.HasValue || !input.startY.HasValue ||
                    !input.endX.HasValue || !input.endY.HasValue ||
                    !TryParseDirection(input.startDirection, out startDirection) ||
                    !TryParseDirection(input.endDirection, out endDirection))
                    return new ApiResponse(400,
                        "{\"success\":false,\"error\":\"Expected motorway endpoints and directions\"}");
                Vector2Int start = new Vector2Int(input.startX.Value, input.startY.Value);
                Vector2Int end = new Vector2Int(input.endX.Value, input.endY.Value);
                if (start == end)
                    return new ApiResponse(400,
                        "{\"success\":false,\"error\":\"Motorway endpoints must differ\"}");
                City city = scope.Get<City>();
                if (city == null)
                    return new ApiResponse(503,
                        "{\"success\":false,\"error\":\"City is unavailable\"}");
                result = editor.AddMotorway(tilemapView, city.NextMotorwayId,
                    tilemapView.GetLowestAvailableMotorwayNumber(), start, startDirection,
                    end, endDirection, -1);
            }
            else
                return new ApiResponse(400, JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = "Unknown action type",
                    type = input.type
                }));

            bool isLegal = result.IsSuccessful;
            string resultCode = result.resultCode.ToString();
            object errorPosition = isLegal ? null : (object)new
            {
                x = result.errorPosition.x,
                y = result.errorPosition.y
            };
            if (result.edit != null)
                scope.Release(result.edit);
            return LegalActionResponse(type, isLegal, resultCode, errorPosition);
        }

        private static bool TryParseDirection(string value, out TileDirection direction)
        {
            return Enum.TryParse(value, true, out direction) &&
                direction != TileDirection.None && (int)direction >= 0 && (int)direction <= 7;
        }

        private static ApiResponse LegalActionResponse(string type, bool legal,
            string resultCode, object errorPosition)
        {
            return new ApiResponse(200, JsonConvert.SerializeObject(new
            {
                success = true,
                type = type,
                legal = legal,
                resultCode = resultCode,
                errorPosition = errorPosition
            }));
        }

        private static bool IsKnownEventReason(string reason)
        {
            return reason == "houseAdded" || reason == "destinationAdded" ||
                reason == "upgradeAvailable" ||
                reason == "overcrowdingWarning" || reason == "overcrowdingStarted" ||
                reason == "terminal";
        }

        private long RecordGameEvent(string reason, object detail,
            Server.ISimulation simulation = null)
        {
            int? tick;
            double? simulationTime;
            if (simulation == null)
                GetCurrentSimulationPosition(out tick, out simulationTime);
            else
                CaptureSimulationPosition(simulation, out tick, out simulationTime);

            GameEventRecord record;
            lock (eventRecordsLock)
            {
                record = eventRecords.Count == 0 ? null : eventRecords[eventRecords.Count - 1];
                if (record == null || record.episodeId != episodeId ||
                    record.simulationTick != tick)
                {
                    record = new GameEventRecord
                    {
                        eventId = ++nextEventId,
                        episodeId = episodeId,
                        simulationTick = tick,
                        simulationTime = simulationTime,
                        timestamp = DateTime.UtcNow
                    };
                    eventRecords.Add(record);
                    if (eventRecords.Count > MaxEventRecords)
                        eventRecords.RemoveAt(0);
                }
                if (!record.reasons.Contains(reason))
                    record.reasons.Add(reason);
                if (detail != null)
                    record.details.Add(detail);
            }

            lock (decisionLock)
            {
                if ((decisionMode == "agent" || decisionMode == "rl") &&
                    autoPauseReasons.Contains(reason))
                {
                    pendingCriticalReasons.Add(reason);
                    pendingCriticalEventId = record.eventId;
                }
            }
            return record.eventId;
        }

        private void EvaluateDecisionPolicy(Server.ISimulation simulation)
        {
            int? tick;
            double? simulationTime;
            CaptureSimulationPosition(simulation, out tick, out simulationTime);
            if (!tick.HasValue || !simulationTime.HasValue)
                return;

            var reasons = new List<string>();
            long? boundaryEventId = null;
            bool intervalDue = false;
            lock (decisionLock)
            {
                if (decisionMode == "off" || decisionPending || decisionCapturePending)
                    return;
                reasons.AddRange(pendingCriticalReasons);
                boundaryEventId = pendingCriticalEventId;
                intervalDue = decisionMode == "rl" &&
                    simulationTime.Value - lastDecisionActionSimulationTime >=
                    minimumSimulationInterval;
            }

            if (intervalDue)
            {
                if (!reasons.Contains("interval"))
                    reasons.Add("interval");
                boundaryEventId = RecordGameEvent("interval", new
                {
                    elapsedSimulationTime = simulationTime.Value -
                        lastDecisionActionSimulationTime
                }, simulation);
            }
            if (reasons.Count == 0)
                return;

            lock (decisionLock)
            {
                if (decisionMode == "off" || decisionPending || decisionCapturePending)
                    return;
                decisionCapturePending = true;
                stagedDecisionEventId = boundaryEventId;
                stagedDecisionTick = tick;
                stagedDecisionSimulationTime = simulationTime;
                stagedDecisionReasons = reasons;
                pendingCriticalReasons.Clear();
                pendingCriticalEventId = null;
            }

            GameUIScreen gameUI = UnityEngine.Object.FindObjectOfType<GameUIScreen>();
            if (gameUI != null && gameUI.GetTimeScaleMode().ToString() != "Paused")
                gameUI.OnPausePressed();
            AdvancePendingDecisionBoundary();
        }

        private void AdvancePendingDecisionBoundary()
        {
            lock (decisionLock)
            {
                if (!decisionCapturePending || decisionMode == "off")
                    return;
            }

            GameUIScreen gameUI = UnityEngine.Object.FindObjectOfType<GameUIScreen>();
            if (gameUI == null || gameUI.GetTimeScaleMode().ToString() != "Paused")
                return;

            GameSnapshot snapshot = CaptureSnapshot();
            lock (decisionLock)
            {
                // Pause transitions can take another Unity update. Publish the
                // boundary only after the game reports Paused, and publish its
                // immutable snapshot atomically with the decision metadata.
                if (!decisionCapturePending || decisionMode == "off" || decisionPending)
                    return;
                decisionPending = true;
                decisionCapturePending = false;
                decisionEventId = stagedDecisionEventId;
                decisionTick = stagedDecisionTick;
                decisionSimulationTime = stagedDecisionSimulationTime;
                decisionReasons = stagedDecisionReasons;
                stagedDecisionEventId = null;
                stagedDecisionTick = null;
                stagedDecisionSimulationTime = null;
                stagedDecisionReasons = new List<string>();
                decisionSnapshot = snapshot;
            }
        }

        private static bool TryGetUpgradeState(out GameUpgradeScreen upgradeScreen,
            out UpgradeDatabaseModel database, out int[] buttonMapping)
        {
            upgradeScreen = UnityEngine.Object.FindObjectOfType<GameUpgradeScreen>();
            var gameUI = UnityEngine.Object.FindObjectOfType<GameUIScreen>();
            var source = (UnityEngine.Component)upgradeScreen ?? gameUI;
            var simulation = source == null ? null :
                Traverse.Create(source).Field("_simulation").GetValue<Server.ISimulation>();
            database = simulation == null ? null : simulation.GetModel<UpgradeDatabaseModel>();
            buttonMapping = upgradeScreen == null ? null :
                Traverse.Create(upgradeScreen).Field("_buttonIndexToUpgradeIndex").GetValue<int[]>();
            return database != null;
        }

        private static bool UpgradeScreenIsReady(GameUpgradeScreen screen, int[] mapping)
        {
            return screen != null && mapping != null &&
                !screen.IsTransitioningIn() && !screen.IsTransitioningOut() &&
                Traverse.Create(screen).Field("_delayTimer").GetValue<float>() < 0f &&
                !Traverse.Create(screen).Field("_hasPopped").GetValue<bool>();
        }

        private static ApiResponse GetUpgradeChoices()
        {
            GameUpgradeScreen screen;
            UpgradeDatabaseModel database;
            int[] mapping;
            bool available = TryGetUpgradeState(out screen, out database, out mapping);
            bool pending = available && database.pendingUpgradeChoices.Count > 0;
            var options = new List<object>();
            if (pending)
            {
                var choices = database.pendingUpgradeChoices[0].choices;
                for (int i = 0; i < choices.Count; i++)
                {
                    var package = choices[i];
                    options.Add(new
                    {
                        optionIndex = i,
                        type = package.type.ToString(),
                        amount = package.amount,
                        additionalConcrete = package.additionalConcrete,
                        selectable = mapping != null && Array.IndexOf(mapping, i) >= 0
                    });
                }
            }
            return new ApiResponse(200, JsonConvert.SerializeObject(new
            {
                success = true,
                pending = pending,
                pendingCount = available ? database.pendingUpgradeChoices.Count : 0,
                ready = pending && UpgradeScreenIsReady(screen, mapping),
                options = options
            }));
        }

        private static ApiResponse SelectUpgrade(string body)
        {
            UpgradeChoiceRequest input;
            try { input = JsonConvert.DeserializeObject<UpgradeChoiceRequest>(body); }
            catch (JsonException)
            {
                return new ApiResponse(400, "{\"success\":false,\"error\":\"Invalid JSON\"}");
            }
            if (input == null || !input.optionIndex.HasValue || input.optionIndex.Value < 0)
                return new ApiResponse(400,
                    "{\"success\":false,\"error\":\"Expected nonnegative optionIndex\"}");

            GameUpgradeScreen screen;
            UpgradeDatabaseModel database;
            int[] mapping;
            if (!TryGetUpgradeState(out screen, out database, out mapping) ||
                database.pendingUpgradeChoices.Count == 0)
                return new ApiResponse(409,
                    "{\"success\":false,\"error\":\"No upgrade choice is pending\"}");
            if (!UpgradeScreenIsReady(screen, mapping))
                return new ApiResponse(409,
                    "{\"success\":false,\"error\":\"Upgrade screen is not ready\"}");

            var choices = database.pendingUpgradeChoices[0].choices;
            int optionIndex = input.optionIndex.Value;
            if (optionIndex >= choices.Count || Array.IndexOf(mapping, optionIndex) < 0)
                return new ApiResponse(422,
                    "{\"success\":false,\"error\":\"Upgrade option is unavailable\"}");

            int buttonIndex = Array.IndexOf(mapping, optionIndex);
            int previousChoicesMade = database.numChoicesMade;
            int previousPendingCount = database.pendingUpgradeChoices.Count;
            var selected = choices[optionIndex];
            screen.OnUpgradeSelect(buttonIndex);
            if (database.numChoicesMade == previousChoicesMade &&
                database.pendingUpgradeChoices.Count == previousPendingCount)
                return new ApiResponse(409,
                    "{\"success\":false,\"error\":\"Game did not accept upgrade selection\"}");
            return new ApiResponse(202, JsonConvert.SerializeObject(new
            {
                success = true,
                scheduled = true,
                resultCode = "Scheduled",
                optionIndex = optionIndex,
                type = selected.type.ToString(),
                amount = selected.amount,
                additionalConcrete = selected.additionalConcrete
            }));
        }

        private ApiResponse ApplyTileEdit(Func<TileEditor, TilemapView, TileEditResult> createEdit,
            Action beforeApply = null)
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
            {
                if (result.edit != null)
                    scope.Release(result.edit);
                return new ApiResponse(422, JsonConvert.SerializeObject(new
                {
                    success = false,
                    resultCode = result.resultCode.ToString(),
                    errorPosition = new { x = result.errorPosition.x, y = result.errorPosition.y }
                }));
            }

            if (result.edit == null)
                return new ApiResponse(200, "{\"success\":true,\"scheduled\":false,\"resultCode\":\"NoChange\"}");

            if (beforeApply != null)
            {
                try { beforeApply(); }
                catch
                {
                    scope.Release(result.edit);
                    throw;
                }
            }

            // Match MotorwaysPlayerAction.AddTileEdit(Immediate): add the client
            // edit to the tile views, schedule the simulation command, then
            // track the pending edit in the client upgrade database.
            ClientTileEdit clientEdit = tilemapView.GenerateClientTileEditAndAddEditToViews(result.edit, false);
            EditTileCommand command = EditTileCommand.Create(scope, result.edit);
            bool scheduled = simulation.ScheduleCommand(command);
            clientEdit.isScheduledOnSimulation = scheduled;
            if (scheduled)
            {
                clientUpgrades.AddTileEdit(clientEdit);
                RegisterScheduledEdit(command, activeActionId, simulation);
            }
            return new ApiResponse(scheduled ? 202 : 409, JsonConvert.SerializeObject(new
            {
                success = scheduled,
                scheduled = scheduled,
                resultCode = scheduled ? "Scheduled" : "ScheduleRejected"
            }));
        }

        private void RegisterScheduledEdit(EditTileCommand command, string actionId,
            Server.ISimulation simulation)
        {
            if (command == null || actionId == null)
                return;

            int? tick;
            double? simulationTime;
            CaptureSimulationPosition(simulation, out tick, out simulationTime);
            lock (actionRecordsLock)
            {
                ActionRecord record;
                if (!actionRecords.TryGetValue(actionId, out record))
                    return;
                pendingEditActions[command] = actionId;
                record.scheduledCommands++;
                record.state = "scheduled";
                if (!record.scheduledAt.HasValue)
                {
                    record.scheduledAt = DateTime.UtcNow;
                    record.scheduledTick = tick;
                    record.scheduledSimulationTime = simulationTime;
                }
            }
        }

        private void MarkEditFinished(EditTileCommand command, Server.ISimulation simulation,
            Exception exception)
        {
            if (command == null)
                return;

            int? tick;
            double? simulationTime;
            CaptureSimulationPosition(simulation, out tick, out simulationTime);
            lock (actionRecordsLock)
            {
                string actionId;
                if (!pendingEditActions.TryGetValue(command, out actionId))
                    return;
                pendingEditActions.Remove(command);

                ActionRecord record;
                if (!actionRecords.TryGetValue(actionId, out record))
                    return;
                if (exception == null)
                    record.executedCommands++;
                else
                {
                    record.failedCommands++;
                    record.state = "rejected";
                    record.resultCode = "ExecutionFailed";
                    record.error = exception.GetType().Name + ": " + exception.Message;
                    record.executedAt = DateTime.UtcNow;
                    record.executedTick = tick;
                    record.executedSimulationTime = simulationTime;
                }
                if (record.submissionComplete &&
                    record.failedCommands == 0 &&
                    record.executedCommands >= record.scheduledCommands)
                    MarkActionExecuted(record, tick, simulationTime);
            }
        }

        private bool TryGetGameNavigation(out ScreenStack screenStack,
            out Factory.IScope appScope, out string screenName)
        {
            // Prefer the visible screen: inactive screens can remain in the stack.
            UnityEngine.Component screen = UnityEngine.Object.FindObjectOfType<GameOverScreen>();
            if (screen == null)
                screen = UnityEngine.Object.FindObjectOfType<GameUIScreen>();
            if (screen == null)
                screen = UnityEngine.Object.FindObjectOfType<MapSelectScreen>();
            if (screen == null)
                screen = UnityEngine.Object.FindObjectOfType<MainMenuScreen>();

            screenStack = screen == null ? null :
                Traverse.Create(screen).Field("_screenStack").GetValue<ScreenStack>();
            appScope = screen == null ? null :
                Traverse.Create(screen).Field("_appScope").GetValue<Factory.IScope>();
            var topScreen = screenStack == null ? null : screenStack.GetTopVisibleScreen();
            screenName = topScreen is GameContainerScreen ? nameof(GameUIScreen) :
                topScreen == null ? null : topScreen.GetType().Name;
            return screenStack != null && appScope != null;
        }

        private ApiResponse GetGameMaps()
        {
            ScreenStack screenStack;
            Factory.IScope appScope;
            string screenName;
            if (!TryGetGameNavigation(out screenStack, out appScope, out screenName))
                return new ApiResponse(503,
                    "{\"success\":false,\"error\":\"Game menu services are unavailable\"}");

            MapDatabase database = appScope.Get<MapDatabase>();
            if (database == null || database.MapLibrary == null)
                return new ApiResponse(503,
                    "{\"success\":false,\"error\":\"Map library is unavailable\"}");

            var maps = new List<object>();
            foreach (MapDefinition map in database.MapLibrary.Maps)
                maps.Add(new { map = map.cityName, name = map.mapName });
            return new ApiResponse(200, JsonConvert.SerializeObject(new { success = true, maps = maps }));
        }

        private ApiResponse StartNewGame(string body)
        {
            NewGameRequest input;
            try
            {
                JObject payload = JObject.Parse(body);
                JToken value;
                if (payload.TryGetValue("map", out value) && value.Type != JTokenType.String ||
                    payload.TryGetValue("seed", out value) &&
                    value.Type != JTokenType.Integer && value.Type != JTokenType.Null ||
                    payload.TryGetValue("startPaused", out value) &&
                    value.Type != JTokenType.Boolean)
                    return new ApiResponse(400,
                        "{\"success\":false,\"error\":\"Invalid map, seed or startPaused type\"}");
                input = JsonConvert.DeserializeObject<NewGameRequest>(body);
            }
            catch (JsonException)
            {
                return new ApiResponse(400, "{\"success\":false,\"error\":\"Invalid JSON or seed\"}");
            }
            if (input == null || string.IsNullOrWhiteSpace(input.map))
                return new ApiResponse(400,
                    "{\"success\":false,\"error\":\"Expected map (cityName)\"}");
            if (newGameLaunch != null &&
                (newGameLaunch.State == "loading" || newGameLaunch.State == "starting" ||
                 newGameLaunch.State == "initializing"))
                return new ApiResponse(409,
                    "{\"success\":false,\"error\":\"A new game is already starting\"}");
            if (nextGameSeed.HasValue)
                return new ApiResponse(409,
                    "{\"success\":false,\"error\":\"A game restart is already starting\"}");

            ScreenStack screenStack;
            Factory.IScope appScope;
            string screenName;
            if (!TryGetGameNavigation(out screenStack, out appScope, out screenName))
                return new ApiResponse(503,
                    "{\"success\":false,\"error\":\"Game menu services are unavailable\"}");
            if (screenName == nameof(GameOverScreen))
                return new ApiResponse(409,
                    "{\"success\":false,\"error\":\"Exit the game-over screen before starting a new map\"}");
            var topScreen = screenStack.GetTopVisibleScreen();
            bool canReplace = screenName == nameof(GameUIScreen) &&
                topScreen is GameContainerScreen ||
                screenName == nameof(MapSelectScreen) && topScreen is MapSelectScreen;
            bool canPush = screenName == nameof(MainMenuScreen) &&
                topScreen is MainMenuScreen;
            if (!canReplace && !canPush)
                return new ApiResponse(409,
                    "{\"success\":false,\"error\":\"Wait for the menu or game screen to settle\"}");

            MapDatabase database = appScope.Get<MapDatabase>();
            MapDefinition map = database == null || database.MapLibrary == null ? null :
                database.MapLibrary.GetMapByName(input.map);
            if (map == null)
                return new ApiResponse(404, JsonConvert.SerializeObject(new
                {
                    success = false, error = "Unknown map", map = input.map
                }));

            var starter = new GameStarter(this);
            bool replaceTopScreen = canReplace;
            if (!starter.StartFromMapDefinition(map, GameMode.Normal, 0f,
                replaceTopScreen, input.startPaused))
                return new ApiResponse(503,
                    "{\"success\":false,\"error\":\"Could not load map\"}");

            newGameLaunch = new NewGameLaunch
            {
                Starter = starter,
                ScreenStack = screenStack,
                AppScope = appScope,
                Map = map.cityName,
                Seed = input.seed ?? FixedSimulationSeed,
                SourceScreen = topScreen.GetType().Name,
                StartedAt = Time.realtimeSinceStartup,
                State = "loading"
            };
            return new ApiResponse(202, JsonConvert.SerializeObject(new
            {
                success = true, resultCode = "NewGameLoading",
                map = newGameLaunch.Map, seed = newGameLaunch.Seed,
                startPaused = input.startPaused
            }));
        }

        private void AdvanceNewGameLaunch()
        {
            NewGameLaunch launch = newGameLaunch;
            if (launch == null || launch.State == "ready" || launch.State == "failed")
                return;
            if (Time.realtimeSinceStartup - launch.StartedAt > NewGameLoadTimeoutSeconds)
            {
                nextGameSeed = null;
                launch.State = "failed";
                launch.Error = "Timed out while starting the new game";
                Logger.LogError(launch.Error + " on " + launch.Map);
                return;
            }
            if (launch.State != "loading" || !launch.Starter.CanStart)
                return;

            var topScreen = launch.ScreenStack.GetTopVisibleScreen();
            if (topScreen == null || topScreen.GetType().Name != launch.SourceScreen)
            {
                launch.State = "failed";
                launch.Error = "Screen changed while the map was loading";
                return;
            }

            try
            {
                nextGameSeed = launch.Seed;
                launch.State = "starting";
                if (!launch.Starter.Start(launch.ScreenStack, launch.AppScope))
                {
                    nextGameSeed = null;
                    launch.State = "failed";
                    launch.Error = "The game rejected the new map";
                }
            }
            catch (Exception exception)
            {
                nextGameSeed = null;
                launch.State = "failed";
                launch.Error = "Could not start the new map";
                Logger.LogError(launch.Error + ": " + exception);
            }
        }

        private ApiResponse RestartGame(string body)
        {
            RestartGameRequest input = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    JObject payload = JObject.Parse(body);
                    JToken value;
                    if (payload.TryGetValue("seed", out value) &&
                        value.Type != JTokenType.Integer && value.Type != JTokenType.Null)
                        return new ApiResponse(400,
                            "{\"success\":false,\"error\":\"Invalid seed type\"}");
                    input = payload.ToObject<RestartGameRequest>();
                }
                catch (JsonException)
                {
                    return new ApiResponse(400,
                        "{\"success\":false,\"error\":\"Invalid JSON or seed\"}");
                }
            }
            if (newGameLaunch != null &&
                (newGameLaunch.State == "loading" || newGameLaunch.State == "starting" ||
                 newGameLaunch.State == "initializing") || nextGameSeed.HasValue)
                return new ApiResponse(409,
                    "{\"success\":false,\"error\":\"A game is already starting\"}");

            ulong? seed = input != null && input.seed.HasValue ?
                (ulong?)input.seed.Value : initialSimulationSeed;
            if (!seed.HasValue)
                return new ApiResponse(409,
                    "{\"success\":false,\"error\":\"Current game seed is unavailable\"}");

            var gameOver = UnityEngine.Object.FindObjectOfType<GameOverScreen>();
            if (gameOver != null)
            {
                nextGameSeed = seed;
                try { gameOver.OnRestart(); }
                catch { nextGameSeed = null; throw; }
                tilemap = null;
                return new ApiResponse(202, JsonConvert.SerializeObject(new
                {
                    success = true, resultCode = "RestartRequested", seed = seed.Value
                }));
            }

            var gameUI = UnityEngine.Object.FindObjectOfType<GameUIScreen>();
            if (gameUI == null)
                return new ApiResponse(409,
                    "{\"success\":false,\"error\":\"No active game to restart\"}");

            var screenStack = Traverse.Create(gameUI).Field("_screenStack").GetValue<ScreenStack>();
            var gameScope = Traverse.Create(gameUI).Field("_gameScope").GetValue<Factory.IScope>();
            if (screenStack == null || gameScope == null)
                return new ApiResponse(503,
                    "{\"success\":false,\"error\":\"Game restart services are unavailable\"}");

            // Reuse the game's normal pause-menu restart path. It records the
            // restart, releases the old game scope, preserves the current game
            // mode, and transitions the in-game container into a fresh map.
            PauseScreen pauseScreen = screenStack.PushScreen<PauseScreen>(
                ScreenStack.MotorwaysScreen.Pause, false, gameScope, true);
            if (pauseScreen == null)
                return new ApiResponse(503,
                    "{\"success\":false,\"error\":\"Could not open the game restart screen\"}");

            nextGameSeed = seed;
            try { Traverse.Create(pauseScreen).Method("RestartGame").GetValue(); }
            catch { nextGameSeed = null; throw; }
            tilemap = null;
            return new ApiResponse(202, JsonConvert.SerializeObject(new
            {
                success = true, resultCode = "RestartRequested", seed = seed.Value
            }));
        }

        private ApiResponse SetGamePaused(bool paused)
        {
            var gameUI = UnityEngine.Object.FindObjectOfType<GameUIScreen>();
            if (gameUI == null)
                return new ApiResponse(409,
                    "{\"success\":false,\"error\":\"No active game\"}");

            string previousMode = gameUI.GetTimeScaleMode().ToString();
            if (paused)
                gameUI.OnPausePressed();
            else
            {
                gameUI.OnPlayPressed();
                lock (decisionLock)
                {
                    decisionPending = false;
                    decisionCapturePending = false;
                    decisionEventId = null;
                    decisionTick = null;
                    decisionSimulationTime = null;
                    decisionReasons.Clear();
                    stagedDecisionEventId = null;
                    stagedDecisionTick = null;
                    stagedDecisionSimulationTime = null;
                    stagedDecisionReasons.Clear();
                    decisionSnapshot = null;
                }
            }

            string currentMode = gameUI.GetTimeScaleMode().ToString();
            return new ApiResponse(200, JsonConvert.SerializeObject(new
            {
                success = true,
                changed = previousMode != currentMode,
                previousMode = previousMode,
                currentMode = currentMode
            }));
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
                episodeId = episodeId,
                terminalReason = terminalReason,
                terminalDestinationBuildingId = terminalDestinationBuildingId,
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

            var currentGame = Traverse.Create(gameUI).Field("_game").GetValue<MotorwaysGame>();
            var city = currentGame == null ? null :
                Traverse.Create(currentGame).Field("_city").GetValue<City>();
            if (currentGame != null && currentGame.MapDefinition != null)
                snapshot.map = currentGame.MapDefinition.cityName;

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
            {
                snapshot.clock = new { time = (double)clock.Time, hour = clock.Hour, day = clock.Day, week = clock.Week };
                if (clock.Clock != null)
                {
                    snapshot.simulationTick = clock.Clock.FrameCount;
                    snapshot.simulationTime = (double)clock.Clock.Time;
                }
            }
            if (city != null && city.Definition != null)
            {
                RectInt full = city.Definition.PlayableArea;
                object current = null;
                if (clock != null)
                {
                    FixMath.RectFixed area = city.GetSimulationPlayableAreaAtTime(
                        clock.Time, City.PlayableAreaRoundingType.ForceWholeTiles);
                    current = new
                    {
                        minX = (double)area.xMin,
                        minY = (double)area.yMin,
                        maxX = (double)area.xMax,
                        maxY = (double)area.yMax
                    };
                }
                snapshot.mapBounds = new
                {
                    full = new
                    {
                        minX = full.xMin,
                        minY = full.yMin,
                        maxX = full.xMax,
                        maxY = full.yMax
                    },
                    current = current
                };
            }

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
                var motorways = Traverse.Create(tilemap).Field("_motorways")
                    .GetValue<Dictionary<int, MotorwayModel>>();
                if (motorways != null)
                {
                    foreach (var entry in motorways)
                    {
                        MotorwayModel motorway = entry.Value;
                        if (motorway == null)
                            continue;
                        Vector2Int start = motorway.StartCoordinates;
                        Vector2Int end = motorway.EndCoordinates;
                        snapshot.motorways.Add(new MotorwaySnapshot
                        {
                            motorwayId = motorway.Id,
                            number = motorway.Number,
                            state = motorway.State.ToString(),
                            hasConsumedUpgrade = motorway.hasConsumedUpgrade,
                            startCoordinates = new { x = start.x, y = start.y },
                            startDirection = motorway.StartDirection.ToString(),
                            endCoordinates = new { x = end.x, y = end.y },
                            endDirection = motorway.EndDirection.ToString()
                        });
                    }
                    snapshot.motorways.Sort((left, right) => left.motorwayId.CompareTo(right.motorwayId));
                }
                var tiles = Traverse.Create(tilemap).Field("_tiles")
                    .GetValue<Dictionary<Vector2Int, TileModel>>();
                if (tiles != null)
                {
                    foreach (var entry in tiles)
                    {
                        if (entry.Value != null && entry.Value.Tile != null)
                            snapshot.tiles.Add(CaptureTile(entry.Key, entry.Value,
                                city, clock));
                    }
                }
            }

            snapshot.motorwayCount = snapshot.motorways.Count;
            snapshot.tileCount = snapshot.tiles.Count;
            if (newGameLaunch != null && newGameLaunch.State == "initializing" &&
                snapshot.map == newGameLaunch.Map && snapshot.initialSeed == newGameLaunch.Seed &&
                snapshot.tileCount > 0)
                newGameLaunch.State = "ready";
            return snapshot;
        }

        private static TileSnapshot CaptureTile(Vector2Int coordinates,
            TileModel tileModel, City city, ClockModel clock)
        {
            Tile tile = tileModel.Tile;
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
                unbuiltMotorwayId = tile.UnbuiltMotorwayId,
                terrain = CaptureTerrain(coordinates, city, clock),
                traffic = CaptureTraffic(tileModel)
            };
        }

        private static object CaptureTerrain(Vector2Int coordinates, City city,
            ClockModel clock)
        {
            CityDefinition definition = city == null ? null : city.Definition;
            if (definition == null)
                return null;
            return new
            {
                buildable = definition.TileIsBuildable(coordinates),
                zoneable = definition.TileIsZoneable(coordinates),
                driveable = definition.TileIsDriveable(coordinates),
                water = definition.TileIsOverWater(coordinates),
                mountain = definition.TileIsUnderAMountain(coordinates),
                currentlyPlayable = clock != null &&
                    city.IsTileInPlayableArea(coordinates, clock.Time)
            };
        }

        private static object CaptureTraffic(TileModel tileModel)
        {
            RoadChunkModel chunk = tileModel == null ? null : tileModel.roadChunk;
            if (chunk == null || chunk.lanes == null || chunk.lanes.Count == 0)
                return null;

            var vehicleIds = new HashSet<int>();
            int stoppedVehicles = 0;
            double totalSpeed = 0d;
            foreach (LaneModel lane in chunk.lanes)
            {
                if (lane == null || lane.Vehicles == null)
                    continue;
                foreach (VehicleModel vehicle in lane.Vehicles)
                {
                    if (vehicle == null)
                        continue;
                    int vehicleId = Traverse.Create(vehicle).Field("id").GetValue<int>();
                    if (!vehicleIds.Add(vehicleId))
                        continue;
                    object frame = vehicle.CurrentFrame;
                    double speed = frame == null ? 0d :
                        (double)Traverse.Create(frame).Field("speed").GetValue<FixMath.Fix64>();
                    totalSpeed += speed;
                    if (speed <= 0.01d)
                        stoppedVehicles++;
                }
            }
            return new
            {
                laneCount = chunk.lanes.Count,
                vehicleCount = vehicleIds.Count,
                stoppedVehicleCount = stoppedVehicles,
                averageSpeed = vehicleIds.Count == 0 ? 0d : totalSpeed / vehicleIds.Count
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

        private string SerializeResponse(GameSnapshot snapshot, string path)
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
                        episodeId = snapshot.episodeId,
                        simulationTick = snapshot.simulationTick,
                        simulationTime = snapshot.simulationTime,
                        count = snapshot.tileCount, tiles = snapshot.tiles, timestamp = snapshot.timestamp
                    });
                case "/tiles/count":
                    return JsonConvert.SerializeObject(new
                    {
                        success = true, snapshotId = snapshot.snapshotId,
                        episodeId = snapshot.episodeId,
                        simulationTick = snapshot.simulationTick,
                        simulationTime = snapshot.simulationTime,
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
                        episodeId = snapshot.episodeId,
                        simulationTick = snapshot.simulationTick,
                        simulationTime = snapshot.simulationTime,
                        count = destinations.Count, destinations = destinations, timestamp = snapshot.timestamp
                    });
                case "/game/status":
                    return JsonConvert.SerializeObject(new
                    {
                        success = true, snapshotId = snapshot.snapshotId,
                        episodeId = snapshot.episodeId,
                        simulationTick = snapshot.simulationTick,
                        simulationTime = snapshot.simulationTime,
                        terminalReason = snapshot.terminalReason,
                        terminalDestinationBuildingId = snapshot.terminalDestinationBuildingId,
                        editCommandsExecuted = snapshot.editCommandsExecuted,
                        phase = snapshot.phase, gameUI = snapshot.gameUI,
                        tilemapView = snapshot.tilemapView, camera = snapshot.camera,
                        timeScaleMode = snapshot.timeScaleMode,
                        initialSeed = snapshot.initialSeed, map = snapshot.map,
                        tileCount = snapshot.tileCount, score = snapshot.score,
                        clock = snapshot.clock, resources = snapshot.resources,
                        newGame = newGameLaunch == null ? null : new
                        {
                            map = newGameLaunch.Map,
                            seed = newGameLaunch.Seed,
                            state = newGameLaunch.State,
                            error = newGameLaunch.Error
                        },
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
                request.TryExpire();

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

        [HarmonyFinalizer, HarmonyPatch(typeof(EditTileCommand), "Execute")]
        private static Exception OnEditCommandFinished(EditTileCommand __instance,
            Server.ISimulation simulation, Exception __exception)
        {
            if (pluginInstance != null)
                pluginInstance.MarkEditFinished(__instance, simulation, __exception);
            return __exception;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(InitCityCommand), "CreateNormalCity")]
        private static void UseFixedSimulationSeed(ref uint seed)
        {
            if (pluginInstance != null)
            {
                bool requested = pluginInstance.nextGameSeed.HasValue &&
                    pluginInstance.nextGameSeed.Value <= uint.MaxValue;
                seed = requested ?
                    (uint)pluginInstance.nextGameSeed.Value : FixedSimulationSeed;
                pluginInstance.Logger.LogInfo((requested ? "Using requested simulation seed " :
                    "Using fixed simulation seed ") + seed);
            }
            else
                seed = FixedSimulationSeed;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(InitCityCommand), "Initialize")]
        private static void CaptureInitialSimulationSeed(ref ulong seed)
        {
            if (pluginInstance != null)
            {
                if (pluginInstance.nextGameSeed.HasValue)
                {
                    seed = pluginInstance.nextGameSeed.Value;
                    pluginInstance.nextGameSeed = null;
                }
                // snapshotId identifies captures within one game. Reset it at
                // the city initialization boundary so API restarts, UI
                // restarts, and newly started games all begin at snapshot 1.
                pluginInstance.snapshotId = 0;
                pluginInstance.initialSimulationSeed = seed;
                pluginInstance.BeginEpisode();
                if (pluginInstance.newGameLaunch != null &&
                    pluginInstance.newGameLaunch.State == "ready")
                    pluginInstance.newGameLaunch = null;
                if (pluginInstance.newGameLaunch != null &&
                    pluginInstance.newGameLaunch.State == "starting" &&
                    seed == pluginInstance.newGameLaunch.Seed)
                    pluginInstance.newGameLaunch.State = "initializing";
            }
        }

        private void BeginEpisode()
        {
            string previousEpisodeId = episodeId;
            string newEpisodeId = Guid.NewGuid().ToString("N");
            lock (actionRecordsLock)
            {
                foreach (ActionRecord record in actionRecords.Values)
                {
                    if (record.awaitingEpisodeStart && record.state == "scheduled")
                    {
                        record.resultingEpisodeId = newEpisodeId;
                        MarkActionExecuted(record, 0, 0d);
                        continue;
                    }
                    if (record.episodeId == previousEpisodeId &&
                        (record.state == "queued" || record.state == "scheduled"))
                    {
                        record.state = "expired";
                        record.resultCode = "EpisodeChanged";
                        record.error = "Episode ended before this action executed";
                    }
                }
                pendingEditActions.Clear();
            }
            episodeId = newEpisodeId;
            terminalReason = null;
            terminalDestinationBuildingId = null;
            activeOvercrowdingDestinations.Clear();
            warnedOvercrowdingDestinations.Clear();
            lock (decisionLock)
            {
                lastDecisionActionSimulationTime = 0d;
                decisionPending = false;
                decisionCapturePending = false;
                decisionEventId = null;
                decisionTick = null;
                decisionSimulationTime = null;
                decisionReasons.Clear();
                stagedDecisionEventId = null;
                stagedDecisionTick = null;
                stagedDecisionSimulationTime = null;
                stagedDecisionReasons.Clear();
                pendingCriticalReasons.Clear();
                pendingCriticalEventId = null;
                decisionSnapshot = null;
            }
            Logger.LogInfo("Started API episode " + episodeId);
        }

        [HarmonyPrefix, HarmonyPatch(typeof(GameplayEventHandler), "EndGame")]
        private static void CaptureTerminalReason(GameplayEventHandler __instance,
            DestinationView failedOnDestination)
        {
            if (pluginInstance == null)
                return;
            pluginInstance.terminalReason = failedOnDestination == null ?
                "gameEnded" : "overcrowding";
            pluginInstance.terminalDestinationBuildingId = failedOnDestination == null ?
                null : GetDestinationBuildingId(failedOnDestination.Model);
            Server.ISimulation simulation = Traverse.Create(__instance)
                .Field("_simulation").GetValue<Server.ISimulation>();
            pluginInstance.RecordGameEvent("terminal", new
            {
                reason = pluginInstance.terminalReason,
                destinationBuildingId = pluginInstance.terminalDestinationBuildingId
            }, simulation);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(HouseModel), "Initialize")]
        private static void OnHouseInitialized(HouseModel __instance)
        {
            if (pluginInstance == null)
                return;
            Server.ISimulation simulation = Traverse.Create(__instance)
                .Field("_simulation").GetValue<Server.ISimulation>();
            pluginInstance.RecordGameEvent("houseAdded", new
            {
                buildingId = GetHouseBuildingId(__instance),
                groupIndex = __instance.GroupIndex
            }, simulation);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(DestinationModel), "Initialize")]
        private static void OnDestinationInitialized(DestinationModel __instance)
        {
            if (pluginInstance == null)
                return;
            Server.ISimulation simulation = Traverse.Create(__instance)
                .Field("_simulation").GetValue<Server.ISimulation>();
            pluginInstance.RecordGameEvent("destinationAdded", new
            {
                buildingId = GetDestinationBuildingId(__instance),
                groupIndex = __instance.GroupIndex
            }, simulation);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(UpgradeDatabaseModel), "AddPendingUpgradeChoice")]
        private static void OnUpgradeAvailable(UpgradeDatabaseModel __instance)
        {
            if (pluginInstance != null)
                pluginInstance.RecordGameEvent("upgradeAvailable", new
                {
                    pendingCount = __instance.pendingUpgradeChoices.Count
                });
        }

        [HarmonyPostfix, HarmonyPatch(typeof(DestinationView), "CreateImminentFailAlert")]
        private static void OnOvercrowdingWarning(DestinationView __instance)
        {
            if (pluginInstance == null || __instance == null)
                return;
            string buildingId = GetDestinationBuildingId(__instance.Model);
            if (buildingId == null ||
                !pluginInstance.warnedOvercrowdingDestinations.Add(buildingId))
                return;
            pluginInstance.RecordGameEvent("overcrowdingWarning", new
            {
                destinationBuildingId = buildingId
            });
        }

        [HarmonyPostfix, HarmonyPatch(typeof(DestinationModel), "SetNextFrameOvercrowdingSpeed")]
        private static void OnOvercrowdingSpeedChanged(DestinationModel __instance,
            FixMath.Fix64 speed)
        {
            if (pluginInstance == null || __instance == null)
                return;
            string buildingId = GetDestinationBuildingId(__instance);
            if ((double)speed > 0d)
            {
                if (buildingId != null &&
                    pluginInstance.activeOvercrowdingDestinations.Add(buildingId))
                {
                    Server.ISimulation simulation = Traverse.Create(__instance)
                        .Field("_simulation").GetValue<Server.ISimulation>();
                    pluginInstance.RecordGameEvent("overcrowdingStarted", new
                    {
                        destinationBuildingId = buildingId
                    }, simulation);
                }
            }
            else if (buildingId != null)
            {
                pluginInstance.activeOvercrowdingDestinations.Remove(buildingId);
                pluginInstance.warnedOvercrowdingDestinations.Remove(buildingId);
            }
        }

        [HarmonyPostfix, HarmonyPatch(typeof(MotorwaysGame), "Tick")]
        private static void OnSimulationTickFinished(MotorwaysGame __instance)
        {
            if (pluginInstance != null && __instance != null && !__instance.HasGameEnded)
                pluginInstance.EvaluateDecisionPolicy(__instance.Simulation);
        }

        [HarmonyPrefix, HarmonyPatch(typeof(PauseScreen), "RestartGame")]
        private static void PreserveSeedOnPauseRestart()
        {
            PreserveSeedOnUiRestart();
        }

        [HarmonyPrefix, HarmonyPatch(typeof(GameOverScreen), nameof(GameOverScreen.OnRestart))]
        private static void PreserveSeedOnGameOverRestart()
        {
            PreserveSeedOnUiRestart();
        }

        private static void PreserveSeedOnUiRestart()
        {
            if (pluginInstance != null && !pluginInstance.nextGameSeed.HasValue)
                pluginInstance.nextGameSeed = pluginInstance.initialSimulationSeed;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(HttpListenerRequest), "FinishInitialization")]
        private static void AcceptBodylessControlPost(HttpListenerRequest __instance)
        {
            // Mono returns HTTP 411 for every POST without Content-Length or
            // Transfer-Encoding, even when the endpoint explicitly has no
            // request body. Limit the compatibility override to the three
            // bodyless local control endpoints; JSON edit actions retain the
            // runtime's normal request-framing validation.
            if (__instance == null ||
                !string.Equals(__instance.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) ||
                __instance.Headers["Content-Length"] != null ||
                __instance.Headers["Transfer-Encoding"] != null)
            {
                return;
            }

            string rawUrl = __instance.RawUrl;
            if (string.IsNullOrEmpty(rawUrl))
                return;

            int queryIndex = rawUrl.IndexOf('?');
            string path = queryIndex < 0 ? rawUrl : rawUrl.Substring(0, queryIndex);
            if (path != "/game/pause" && path != "/game/resume" &&
                path != "/game/restart" && path != "/actions/noop")
                return;

            Traverse request = Traverse.Create(__instance);
            request.Field("cl_set").SetValue(true);
            request.Field("content_length").SetValue(0L);
        }

        [HarmonyPrefix, HarmonyPatch(typeof(GameUIScreen), nameof(GameUIScreen.SetWorldGridActive))]
        private static void KeepWorldGridVisible(ref bool active)
        {
            if (pluginInstance != null && pluginInstance.alwaysShowWorldGrid)
                active = true;
        }
    }
}
