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
            // 获取GameUI实例（假设是单例）
            //var instanceProp = AccessTools.Property(typeof(GameUIScreen), "Instance");
            //uiInstance = instanceProp?.GetValue(null);
            
        }

        private void Update()
        {
            try
            {
                object inGameUI = UnityEngine.Object.FindObjectOfType(typeof(GameUIScreen));
                object inGameTilemapView = UnityEngine.Object.FindObjectOfType(typeof(TilemapView));
                if (inGameUI != null)
                {
                    Debug.Log($"Awake to get UI instace: {inGameUI}");
                    Debug.Log($"Awake to get inGamePlayerAction instace: {inGameTilemapView}");
                    setGridMethod.Invoke(inGameUI, new object[] { true, TransitionStyle.Tween });
                    Debug.Log("Runing invoke.");
                }
                
            }
            catch (Exception e) { 
                Debug.Log(e);
            }
        }

        //[HarmonyPrefix, HarmonyPatch(typeof(GameUIScreen), "SetMotorwayGridActive")]
        //public static bool GameUIScreen_SetMotorwayGridActive(GameUIScreen __instance, ref bool active, TransitionStyle transitionStyle = TransitionStyle.Tween)
        //{
        //    var traverse = Traverse.Create(__instance);

        //    var _isForceHidden = traverse.Field("_isForceHidden").GetValue<bool>();
        //    var _isWorldGridForceHidden = traverse.Field("_isWorldGridForceHidden").GetValue<bool>();
        //    //active = true;
        //    Debug.Log($"We inspect: _isForceHidden:{_isForceHidden}, _isWorldGridForceHidden: {_isWorldGridForceHidden}");
        //    return false;
        //}

        [HarmonyPostfix, HarmonyPatch(typeof(Motorways.Models.TilemapModel), "GetAllTileCoordinates")]
        public static void MotorwaysModelsTilemapModel_GetAllTileCoordinates(Motorways.Models.TilemapModel __instance, ref IEnumerable<Vector2Int> __result)
        {
            
            try
            {

                var traverse = Traverse.Create(__instance);
                
                var tilesDict = traverse.Field("_tiles").GetValue<Dictionary<Vector2Int, TileModel>>();

                
                if (tilesDict != null)
                {
                    //Debug.Log($"找到 {tilesDict.Count} 个 tiles");

                    foreach (var kvp in tilesDict)
                    {
                        Debug.Log(__instance.GetTile(kvp.Key));
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
