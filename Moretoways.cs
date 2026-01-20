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

namespace Moretoways
{
    [BepInPlugin("Nul4i.motoways.plugin", "Moretoways", "1.0.0")]
    public class Moretoways : BaseUnityPlugin
    {
        void Start()
        {
            Debug.Log("Unity: start");
            Logger.LogInfo("hello");
            Harmony.CreateAndPatchAll(typeof(Moretoways));  
        }

        static private MethodInfo setGridMethod;
        private object uiInstance;

        private void Awake()
        {
            // 使用AccessTools查找方法和实例
            setGridMethod = AccessTools.Method(
                typeof(GameUIScreen),
                "SetWorldGridActive",
                new Type[] { typeof(bool), typeof(TransitionStyle) }
                );

            
            if (setGridMethod == null)
            {
                Debug.Log("Cannot find `SetWorldGridActive` Method");
                return;
            }
            else
            {
                Debug.Log("Found SetWorldGridActive.");
            }
            
        }

        private void Update()
        {
            try
            {
                object inGameUI = UnityEngine.Object.FindObjectOfType(typeof(GameUIScreen));
                object inGameTilemapView = UnityEngine.Object.FindObjectOfType(typeof(TilemapView));
                if (inGameUI != null)
                {
                    //Debug.Log($"Awake to get UI instace: {inGameUI}");
                    //Debug.Log($"Awake to get inGamePlayerAction instace: {inGameTilemapView}");
                    setGridMethod.Invoke(inGameUI, new object[] { true, TransitionStyle.Tween });
                    //Debug.Log("Runing invoke.");
                }
                
            }
            catch (Exception e) { 
                Debug.Log(e);
            }
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Motorways.Models.TilemapModel), "GetAllTileCoordinates")]
        public static void MotorwaysModelsTilemapModel_GetAllTileCoordinates(Motorways.Models.TilemapModel __instance, ref IEnumerable<Vector2Int> __result)
        {
            // this only works when ingame update tiles. for user operation may need to see add tile.
            try
            {

                var traverse = Traverse.Create(__instance);
                
                var tilesDict = traverse.Field("_tiles").GetValue<Dictionary<Vector2Int, TileModel>>();

                

                
                if (tilesDict != null)
                {
                    //Debug.Log($"找到 {tilesDict.Count} 个 tiles");
                    
                    foreach (var kvp in tilesDict)
                    {
                        //Debug.Log(kvp.Key); // Vector2Int
                        
                        Tile tile = __instance.GetTile(kvp.Key);
                        //Debug.Log(tilemodel); // TileModel
                        Debug.Log(tile.Coordinates);
                        Debug.Log(tile.ContentType);
                        Debug.Log(tile.ContentModel);
                        try
                        {
                            if (tile.ContentType == TileContentType.Destination)
                            {
                                var destion = (DestinationModel)tile.ContentModel;
                                Debug.Log(destion.GroupIndex);
                            }
                        }
                        catch (Exception e)
                        {
                            Debug.Log(e);
                        }
                        


                        //Debug.Log(kvp.Value.WorldPosition);
                        var coordinates = kvp.Key;
                        Debug.Log(Camera.main.WorldToScreenPoint(TilemapView.GetWorldPositionForCoordinates(coordinates)));
                        //Debug.Log()
                        //Debug.Log($"坐标: ({kvp.Key.x}, {kvp.Key.y}), 数据: {kvp.Value}");
                    }
                }
                Debug.Log("========================== Update finished ======================");
            }
            catch (Exception e) {
                Debug.Log("Error: running getAllTileCoordinates.");
            }            
        }
    }
}
