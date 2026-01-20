using BepInEx;
using BepInEx.Unity.Mono;
using HarmonyLib;
using Motorways;
using Motorways.Actions;
using Motorways.Models;
using Motorways.Views;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using System.Net;
using System.IO;
using System.Threading;

namespace Moretoways
{
    [BepInPlugin("Nul4i.motoways.plugin", "Moretoways", "1.0.0")]
    public class Moretoways : BaseUnityPlugin
    {
        private HttpListener httpListener;
        private Thread httpThread;
        private bool isRunning = true;
        private Dictionary<Vector2Int, TileModel> cachedTiles = new Dictionary<Vector2Int, TileModel>();
        private readonly object cacheLock = new object();
        private static Moretoways pluginInstance;

        void Start()
        {
            Debug.Log("Unity: start");
            pluginInstance = this;
            Logger.LogInfo("hello");
            Harmony.CreateAndPatchAll(typeof(Moretoways));

            // 启动HTTP服务器
            StartHttpServer();
        }

        private void StartHttpServer()
        {
            try
            {
                // 创建HTTP监听器，监听本地9000端口
                httpListener = new HttpListener();
                httpListener.Prefixes.Add("http://localhost:9000/");
                httpListener.Start();

                Debug.Log("RESTful服务已启动，地址: http://localhost:9000/");

                // 在后台线程运行HTTP服务器
                httpThread = new Thread(ListenForRequests);
                httpThread.IsBackground = true;
                httpThread.Start();
            }
            catch (Exception e)
            {
                Debug.LogError($"启动HTTP服务器失败: {e.Message}");
            }
        }

        private void ListenForRequests()
        {
            while (isRunning && httpListener != null && httpListener.IsListening)
            {
                try
                {
                    // 异步等待请求
                    var context = httpListener.GetContext();
                    ProcessRequest(context);
                }
                catch (Exception e)
                {
                    if (isRunning)
                        Debug.LogError($"HTTP请求处理错误: {e.Message}");
                }
            }
        }

        private void ProcessRequest(HttpListenerContext context)
        {
            try
            {
                var request = context.Request;
                var response = context.Response;

                // 设置CORS头
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

                // 处理预检请求
                if (request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = 200;
                    response.Close();
                    return;
                }

                string responseString = "";
                response.ContentType = "application/json";

                // 根据请求路径路由到不同的处理函数
                switch (request.Url.AbsolutePath)
                {
                    case "/":
                    case "/tiles":
                        Debug.Log("Access /tiles");
                        responseString = GetAllTilesJson();
                        break;

                    case "/tiles/count":
                        Debug.Log("Access /tiles/count");
                        responseString = GetTilesCountJson();
                        break;

                    case "/destinations":
                        Debug.Log("Access /destinations");
                        responseString = GetDestinationsJson();
                        break;

                    case "/game/status":
                        Debug.Log("Access /game/status");
                        responseString = GetGameStatusJson();
                        break;

                    default:
                        response.StatusCode = 404;
                        responseString = "{\"error\": \"Not Found\"}";
                        break;
                }

                // 发送响应
                byte[] buffer = Encoding.UTF8.GetBytes(responseString);
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.OutputStream.Close();
            }
            catch (Exception e)
            {
                Debug.LogError($"处理HTTP请求时出错: {e}");
            }
        }

        private string GetAllTilesJson()
        {
            lock (cacheLock)
            {
                var tilesList = new List<object>();

                foreach (var kvp in cachedTiles)
                {
                    var tile = kvp.Value;
                    var tileData = new
                    {
                        coordinates = new { x = kvp.Key.x, y = kvp.Key.y },
                        //worldPosition = TilemapView.GetWorldPositionForCoordinates(kvp.Key),
                        //screenPosition = Camera.main != null ?
                        //    (object)new
                        //    {
                        //        x = Camera.main.WorldToScreenPoint(TilemapView.GetWorldPositionForCoordinates(kvp.Key)).x,
                        //        y = Camera.main.WorldToScreenPoint(TilemapView.GetWorldPositionForCoordinates(kvp.Key)).y
                        //    } : null,
                        contentType = tile.Tile.ContentType.ToString(),
                        contentData = GetContentData(tile)
                    };

                    tilesList.Add(tileData);
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = tilesList.Count,
                    tiles = tilesList,
                    timestamp = DateTime.UtcNow
                });
            }
        }

        private object GetContentData(TileModel tile)
        {
            if (tile.Tile.ContentModel == null)
                return null;

            try
            {
                if (tile.Tile.ContentType == TileContentType.Destination)
                {
                    var destination = (DestinationModel)tile.Tile.ContentModel;
                    return new
                    {
                        groupIndex = destination.GroupIndex,
                        type = "Destination"
                    };
                }
                // 可以添加其他类型的处理
                else
                {
                    return new { type = tile.Tile.ContentType.ToString() };
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"获取ContentData时出错: {e.Message}");
                return new { error = e.Message };
            }
        }

        private string GetTilesCountJson()
        {
            Debug.Log(cachedTiles);
            lock (cacheLock)
            {
                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = cachedTiles.Count,
                    timestamp = DateTime.UtcNow
                });
            }
        }

        private string GetDestinationsJson()
        {
            lock (cacheLock)
            {
                var destinations = new List<object>();
                
                foreach (var kvp in cachedTiles)
                {
                    var tile = kvp.Value;
                    //if (tile.Tile.ContentType == TileContentType.Destination && tile.Tile.ContentModel != null)
                    {
                        try
                        {
                            var destination = (DestinationModel)tile.Tile.ContentModel;
                            destinations.Add(new
                            {
                                coordinates = new { x = kvp.Key.x, y = kvp.Key.y },
                                //groupIndex = destination.GroupIndex,
                                //worldPosition = TilemapView.GetWorldPositionForCoordinates(kvp.Key)
                            });
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"处理目的地数据时出错: {e.Message}");
                        }
                    }
                }

                return Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    success = true,
                    count = destinations.Count,
                    destinations = destinations,
                    timestamp = DateTime.UtcNow
                });
            }
        }

        private string GetGameStatusJson()
        {
            var inGameUI = UnityEngine.Object.FindObjectOfType(typeof(GameUIScreen));
            var inGameTilemapView = UnityEngine.Object.FindObjectOfType(typeof(TilemapView));

            return Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                success = true,
                gameUI = inGameUI != null,
                tilemapView = inGameTilemapView != null,
                camera = Camera.main != null,
                tileCount = cachedTiles.Count,
                timestamp = DateTime.UtcNow
            });
        }

        // 更新缓存方法
        private void UpdateTileCache(Dictionary<Vector2Int, TileModel> tilesDict)
        {
            lock (cacheLock)
            {
                cachedTiles.Clear();
                foreach (var kvp in tilesDict)
                {
                    cachedTiles[kvp.Key] = kvp.Value;
                }
            }
        }

        private void OnDestroy()
        {
            // 清理资源
            isRunning = false;

            if (httpListener != null && httpListener.IsListening)
            {
                httpListener.Stop();
                httpListener.Close();
            }

            if (httpThread != null && httpThread.IsAlive)
            {
                httpThread.Join(1000);
            }

            Debug.Log("RESTful服务已停止");
        }

        // Harmony补丁部分保持不变，但更新缓存
        [HarmonyPostfix, HarmonyPatch(typeof(Motorways.Models.TilemapModel), "GetAllTileCoordinates")]
        public static void MotorwaysModelsTilemapModel_GetAllTileCoordinates(Motorways.Models.TilemapModel __instance, ref IEnumerable<Vector2Int> __result)
        {
            try
            {
                var traverse = Traverse.Create(__instance);
                var tilesDict = traverse.Field("_tiles").GetValue<Dictionary<Vector2Int, TileModel>>();

                if (tilesDict != null)
                {
                    // 更新缓存
                    
                    if (pluginInstance != null)
                    {
                        pluginInstance.UpdateTileCache(tilesDict);
                        //foreach (var kvp in pluginInstance.cachedTiles)
                        //{
                        //    Debug.Log(kvp.Key);
                        //    Debug.Log(kvp.Value);
                        //}
                        
                    }

                    // 原有的调试输出代码...
                    foreach (var kvp in tilesDict)
                    {
                        var tilemodel = __instance.GetTile(kvp.Key);
                        // ... 原有的调试代码
                    }
                }
            }
            catch (Exception e)
            {
                Debug.Log("Error: running getAllTileCoordinates.");
            }
        }

        // 原有的其他方法保持不变...
        static private MethodInfo setGridMethod;
        private object uiInstance;

        private void Awake()
        {
            // ... 原有代码
        }

        private void Update()
        {
            // ... 原有代码
        }
    }
}