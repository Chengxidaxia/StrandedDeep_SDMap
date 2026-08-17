using System;
using System.Collections.Generic;
using Beam;
using Funlabs;
using Photon.Bolt;
using UnityEngine;

namespace SDMap
{
    /// <summary>
    /// 标记点（世界坐标 XZ，Y 在添加时按该位置「最高可站立层」自动捕获，海里则为海平面 0）。
    /// 持久化交给 SDMapWaypointStorage（实现 IPersistentSaveable，随游戏存档一起保存/读取），
    /// 不再单独写 JSON 文件。
    /// </summary>
    [Serializable]
    public class Waypoint
    {
        public string Name;
        public float X;
        public float Z;
        public float Y;
        public int ColorIndex;
    }

    [Serializable]
    public class WaypointList
    {
        public List<Waypoint> Items = new List<Waypoint>();
    }

    /// <summary>标记点内存管理与多人（MP）分享。</summary>
    public static class WaypointManager
    {
        private static List<Waypoint> _waypoints = new List<Waypoint>();

        // 路径点可选颜色（索引顺序，改色时循环切换）
        public static readonly Color[] ColorPresets = new Color[]
        {
            new Color(1f, 0.25f, 0.25f, 1f),   // 0 红
            new Color(0.35f, 0.85f, 0.35f, 1f), // 1 绿
            new Color(0.35f, 0.55f, 1f, 1f),    // 2 蓝
            new Color(1f, 0.85f, 0.25f, 1f),    // 3 黄
            new Color(1f, 0.55f, 0.20f, 1f),    // 4 橙
            new Color(0.70f, 0.40f, 1f, 1f),    // 5 紫
            new Color(0.40f, 0.90f, 0.90f, 1f), // 6 青
            new Color(1f, 1f, 1f, 1f),          // 7 白
        };

        public static List<Waypoint> Waypoints
        {
            get { return _waypoints; }
        }

        public static Color GetColor(int index)
        {
            if (index < 0 || index >= ColorPresets.Length)
            {
                return ColorPresets[0];
            }
            return ColorPresets[index];
        }

        /// <summary>
        /// 计算 (x, z) 位置的世界高度：向下 raycast 找「最高可站立层」（地形 / 物体 / 建筑 / 树木等固体）。
        /// 命中点若在海平面之上（y ≥ 0）则返回该点高度；否则（海底）或未命中（海里）一律返回海平面 0。
        /// 注意：水（WATER）不是可站立层，不纳入 raycast。
        /// </summary>
        public static float ComputeY(float x, float z)
        {
            try
            {
                Vector3 origin = new Vector3(x, 500f, z);
                RaycastHit hit;
                int mask = (1 << Layers.TERRAIN) | (1 << Layers.TERRAIN_OBJECTS)
                    | (1 << Layers.CONSTRUCTIONS) | (1 << Layers.CONSTRUCTIONS_SMALL) | (1 << Layers.CONSTRUCTIONS_RAFTS)
                    | (1 << Layers.INTERACTIVE_TREES) | (1 << Layers.INTERACTIVE_OBJECTS);
                if (Physics.Raycast(origin, Vector3.down, out hit, 1500f, mask, QueryTriggerInteraction.Ignore))
                {
                    return hit.point.y >= 0f ? hit.point.y : 0f;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SDMap] ComputeY failed: " + e.Message);
            }
            return 0f;   // 海里 / 未命中 → 海平面
        }

        // ---------- 本地操作（仅内存，持久化由 SDMapWaypointStorage 随存档完成） ----------

        /// <summary>添加路径点（自动计算该位置最高可站立层高度）。</summary>
        public static Waypoint Add(string name, float x, float z)
        {
            return AddRaw(name, x, z, ComputeY(x, z), 0);
        }

        /// <summary>直接以给定数据添加路径点（存档加载 / 分享接收用，不广播、不重复算高）。</summary>
        public static Waypoint AddRaw(string name, float x, float z, float y, int colorIndex)
        {
            Waypoint wp = new Waypoint { Name = name, X = x, Z = z, Y = y, ColorIndex = colorIndex };
            _waypoints.Add(wp);
            return wp;
        }

        public static void Remove(int index)
        {
            if (index >= 0 && index < _waypoints.Count)
            {
                _waypoints.RemoveAt(index);
            }
        }

        public static void Rename(int index, string newName)
        {
            if (index >= 0 && index < _waypoints.Count && !string.IsNullOrEmpty(newName))
            {
                _waypoints[index].Name = newName;
            }
        }

        public static void CycleColor(int index)
        {
            if (index >= 0 && index < _waypoints.Count)
            {
                _waypoints[index].ColorIndex = (_waypoints[index].ColorIndex + 1) % ColorPresets.Length;
            }
        }

        /// <summary>清空全部路径点（存档加载前调用）。</summary>
        public static void Clear()
        {
            _waypoints.Clear();
        }

        // ---------- 分享（手动触发，把完整路径点打包发给其他玩家） ----------

        /// <summary>分享指定索引的路径点（颜色、名称、坐标一次打包，接收方 upsert）。</summary>
        public static void Share(int index)
        {
            if (index < 0 || index >= _waypoints.Count)
            {
                return;
            }
            Waypoint wp = _waypoints[index];
            ShareData(wp.Name, wp.X, wp.Z, wp.Y, wp.ColorIndex);
        }

        /// <summary>分享一个路径点的完整数据（颜色、名称、坐标全包含）。</summary>
        private static void ShareData(string name, float x, float z, float y, int colorIndex)
        {
            try
            {
                if (!IsMPInstalled())
                {
                    return;
                }
                if (!Game.Mode.IsMultiplayer())
                {
                    return;
                }
                if (!BoltNetwork.IsServer && !BoltNetwork.IsClient)
                {
                    return;
                }
                new WaypointShareMessage
                {
                    Name = name,
                    X = x,
                    Z = z,
                    Y = y,
                    ColorIndex = colorIndex
                }.Post();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SDMap] waypoint share failed: " + e.Message);
            }
        }

        /// <summary>接收其他玩家分享的完整路径点：同坐标则更新（名称/颜色/高度），否则新增。</summary>
        public static void ApplyShared(string name, float x, float z, float y, int colorIndex)
        {
            try
            {
                for (int i = 0; i < _waypoints.Count; i++)
                {
                    Waypoint wp = _waypoints[i];
                    if (Mathf.Abs(wp.X - x) < 0.5f && Mathf.Abs(wp.Z - z) < 0.5f)
                    {
                        // 同坐标：更新名称/高度/颜色
                        wp.Name = name;
                        wp.Y = y;
                        wp.ColorIndex = colorIndex;
                        return;
                    }
                }
                // 不存在则新增
                AddRaw(name, x, z, y, colorIndex);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SDMap] ApplyShared failed: " + e.Message);
            }
        }

        // ---------- MP 检测 ----------

        private static bool _mpChecked;
        private static bool _mpInstalled;

        /// <summary>检测是否安装了 MultiplayerPlus 模组（按程序集名判断）。</summary>
        private static bool IsMPInstalled()
        {
            if (_mpChecked)
            {
                return _mpInstalled;
            }
            _mpChecked = true;
            try
            {
                foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name == "MultiplayerPlus")
                    {
                        _mpInstalled = true;
                        break;
                    }
                }
            }
            catch (Exception)
            {
                // ignore
            }
            return _mpInstalled;
        }
    }
}
