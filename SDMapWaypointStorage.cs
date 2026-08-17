using System;
using Beam.Serialization.Json;
using UnityEngine;

namespace SDMap
{
    /// <summary>
    /// 路径点存档组件：实现游戏自带的 IPersistentSaveable（即 ISaveable）。
    /// 游戏存档时（SaveManager.GenerateSave_Sequence）会自动遍历场景里所有 IPersistentSaveable 的 MonoBehaviour，
    /// 调用 Save() 并把结果存到存档的 "Persistent.SDMapWaypointStorage" 字段；读档时（LevelLoader.LoadGame_Sequence）
    /// 自动调用 Load(data) 恢复。这样路径点随游戏存档一起保存/读取，不再单独写 JSON 文件。
    /// </summary>
    public class SDMapWaypointStorage : MonoBehaviour, IPersistentSaveable
    {
        private const string KName = "Name";
        private const string KX = "X";
        private const string KZ = "Z";
        private const string KY = "Y";
        private const string KColor = "ColorIndex";

        /// <summary>序列化路径点列表为 JObject（ARRAY of OBJECT）。</summary>
        public JObject Save()
        {
            JObject arr = new JObject();
            try
            {
                var wps = WaypointManager.Waypoints;
                for (int i = 0; i < wps.Count; i++)
                {
                    Waypoint wp = wps[i];
                    JObject obj = new JObject();
                    obj.AddField(KName, wp.Name ?? "");
                    obj.AddField(KX, wp.X);
                    obj.AddField(KZ, wp.Z);
                    obj.AddField(KY, wp.Y);
                    obj.AddField(KColor, wp.ColorIndex);
                    arr.Add(obj);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SDMap] save waypoints failed: " + e.Message);
            }
            return arr;
        }

        /// <summary>从存档反序列化路径点列表到 WaypointManager。</summary>
        public void Load(JObject data)
        {
            WaypointManager.Clear();
            if (data == null || data.IsNull() || data.Children == null)
            {
                return;
            }
            try
            {
                foreach (JObject child in data.Children)
                {
                    if (child == null || child.IsNull())
                    {
                        continue;
                    }
                    string name = GetString(child, KName);
                    float x = GetFloat(child, KX);
                    float z = GetFloat(child, KZ);
                    float y = GetFloat(child, KY);
                    int colorIndex = GetInt(child, KColor);
                    WaypointManager.AddRaw(name, x, z, y, colorIndex);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SDMap] load waypoints failed: " + e.Message);
            }
        }

        private static string GetString(JObject obj, string key)
        {
            JObject f = obj.GetField(key);
            if (f == null || f.IsNull())
            {
                return "";
            }
            return f.GetValue<string>();
        }

        private static float GetFloat(JObject obj, string key)
        {
            JObject f = obj.GetField(key);
            if (f == null || f.IsNull())
            {
                return 0f;
            }
            return f.GetValue<float>();
        }

        private static int GetInt(JObject obj, string key)
        {
            JObject f = obj.GetField(key);
            if (f == null || f.IsNull())
            {
                return 0;
            }
            return f.GetValue<int>();
        }
    }
}
