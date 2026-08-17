using System.Collections.Generic;
using Beam;
using Funlabs;
using UnityEngine;
using UnityEngine.Rendering;

namespace SDMap
{
    /// <summary>
    /// 路径点的游戏世界内标记：在玩家第一人称视角渲染一条竖直光柱（颜色 = 路径点颜色）+ 顶部名称（billboard 朝向玩家）。
    /// 光柱放在 Default 层（地图相机的 cullingMask 不含 Default），所以只对玩家主相机可见、不会污染地图纹理。
    /// 挂在 SDMap 的一个常驻 GameObject 上，随 WaypointManager 的路径点增删改实时同步。
    /// </summary>
    public class WaypointWorldMarker : MonoBehaviour
    {
        private class Entry
        {
            public GameObject Root;
            public LineRenderer Beam;
            public TextMesh Label;
            public string Name;
            public int ColorIndex;
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private Material _beamMat;
        private Font _font;

        private const float BeamHeight = 150f;   // 光柱高度（世界单位）
        private const float BeamWidth = 2f;      // 光柱宽度
        private const float LabelY = BeamHeight + 6f;   // 名称相对光柱底部的偏移

        private void Awake()
        {
            Shader s = Shader.Find("Sprites/Default");
            if (s != null)
            {
                _beamMat = new Material(s);
            }
            try
            {
                _font = Font.CreateDynamicFontFromOSFont(new string[] { "Microsoft YaHei", "Arial" }, 32);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[SDMap] waypoint marker font failed: " + e.Message);
            }
        }

        private void Update()
        {
            bool inGame = false;
            try
            {
                inGame = Game.State.IsGame();
            }
            catch (System.Exception)
            {
                // ignore
            }
            if (!inGame)
            {
                // 非游戏状态隐藏所有标记
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (_entries[i].Root != null)
                    {
                        _entries[i].Root.SetActive(false);
                    }
                }
                return;
            }

            SyncEntries();
            UpdateTransforms();
        }

        private void SyncEntries()
        {
            List<Waypoint> wps = WaypointManager.Waypoints;
            // 删除多余的
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (i >= wps.Count)
                {
                    DestroyEntry(_entries[i]);
                    _entries.RemoveAt(i);
                }
            }
            // 更新/创建
            for (int i = 0; i < wps.Count; i++)
            {
                Waypoint wp = wps[i];
                if (i < _entries.Count)
                {
                    Entry e = _entries[i];
                    if (!e.Root.activeSelf)
                    {
                        e.Root.SetActive(true);
                    }
                    if (e.Name != wp.Name || e.ColorIndex != wp.ColorIndex)
                    {
                        ApplyStyle(e, wp);
                    }
                }
                else
                {
                    _entries.Add(CreateEntry(wp));
                }
            }
        }

        private Entry CreateEntry(Waypoint wp)
        {
            GameObject root = new GameObject("SDMap_WaypointMarker");
            root.transform.parent = transform;

            LineRenderer beam = root.AddComponent<LineRenderer>();
            if (_beamMat != null)
            {
                beam.material = _beamMat;
            }
            beam.positionCount = 2;
            beam.useWorldSpace = false;
            beam.startWidth = BeamWidth;
            beam.endWidth = BeamWidth;
            beam.SetPosition(0, Vector3.zero);
            beam.SetPosition(1, new Vector3(0f, BeamHeight, 0f));
            beam.shadowCastingMode = ShadowCastingMode.Off;
            beam.receiveShadows = false;

            GameObject labelGo = new GameObject("Label");
            labelGo.transform.parent = root.transform;
            labelGo.transform.localPosition = new Vector3(0f, LabelY, 0f);
            TextMesh label = labelGo.AddComponent<TextMesh>();
            label.anchor = TextAnchor.LowerCenter;
            label.alignment = TextAlignment.Center;
            label.fontSize = 64;
            label.characterSize = 0.8f;
            if (_font != null)
            {
                label.font = _font;
            }

            Entry e = new Entry { Root = root, Beam = beam, Label = label };
            ApplyStyle(e, wp);
            return e;
        }

        private void ApplyStyle(Entry e, Waypoint wp)
        {
            Color c = WaypointManager.GetColor(wp.ColorIndex);
            Color beamColor = new Color(c.r, c.g, c.b, 0.65f);
            if (e.Beam != null)
            {
                e.Beam.startColor = beamColor;
                e.Beam.endColor = beamColor;
            }
            if (e.Label != null)
            {
                e.Label.text = wp.Name;
                e.Label.color = c;
            }
            e.Name = wp.Name;
            e.ColorIndex = wp.ColorIndex;
        }

        private void DestroyEntry(Entry e)
        {
            if (e != null && e.Root != null)
            {
                Destroy(e.Root);
            }
        }

        private void UpdateTransforms()
        {
            Camera cam = Camera.main;
            List<Waypoint> wps = WaypointManager.Waypoints;
            for (int i = 0; i < _entries.Count && i < wps.Count; i++)
            {
                Entry e = _entries[i];
                Waypoint wp = wps[i];
                if (e.Root == null)
                {
                    continue;
                }
                e.Root.transform.position = new Vector3(wp.X, wp.Y, wp.Z);
                if (cam != null && e.Label != null)
                {
                    // 名称 billboard：让文字正面（-Z）朝向玩家相机
                    Vector3 dir = e.Label.transform.position - cam.transform.position;
                    if (dir.sqrMagnitude > 0.0001f)
                    {
                        e.Label.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
                    }
                }
            }
        }
    }
}
