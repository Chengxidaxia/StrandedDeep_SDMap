using System;
using System.Collections.Generic;
using Beam;
using Beam.Terrain;
using Funlabs;
using Rewired;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace SDMap
{
    /// <summary>
    /// 小地图 / 大地图渲染。全彩分层俯视地貌、chevron 无尾箭头、坐标 X/Y/Z、右键菜单、滚轮缩放。
    /// 统一约定：+X 朝右、+Z 朝上；玩家水平朝向 = MouseLook.RotationX（RotationY 是俯仰角，别用错）。
    /// </summary>
    internal static class MapRenderer
    {
        private static Texture2D _whiteTex;
        private static Texture2D _arrowTex;
        private static Font _font;
        private static bool _fontReady;
        private static GUIStyle _labelStyle;
        private static GUIStyle _titleStyle;
        private static GUIStyle _buttonStyle;
        private static GUIStyle _inputStyle;
        private static Vector2 _scroll;

        private static bool _worldMapOpen;
        private static float _mapZoom = 1f;
        private static bool _prevCursorVisible;
        private static CursorLockMode _prevLockMode;

        // 右键菜单
        private static bool _contextMenuOpen;
        private static float _contextWorldX;
        private static float _contextWorldZ;
        private static Vector2 _contextScreenPos;

        // 大地图平移（pan）
        private static float _mapCenterX;
        private static float _mapCenterZ;
        private static bool _mapCenterInit;
        private static bool _panning;
        private static Vector2 _panStartMouse;
        private static float _panStartCenterX;
        private static float _panStartCenterZ;

        // 路径点改名状态
        private static int _renamingIndex = -1;   // 正在改名的路径点索引（-1 = 无）
        private static string _renameText = "";

        private const float WorldMin = -2200f;
        private const float WorldMax = 2200f;
        private const float WorldRange = WorldMax - WorldMin;
        private const float IslandDiameter = 500f;
        private const int TerrainTexSize = 257;      // Legacy 默认纹理分辨率
        private const int TerrainTexSizeHi = 512;    // Legacy 放大时的高分辨率纹理

        // 深海色（与 HeightToColor 深海一致，用作地图背景）
        private static readonly Color DeepSea = new Color(0.10f, 0.30f, 0.55f, 1f);

        // 法线光照的虚拟太阳方向（西北上方斜射）
        private static readonly Vector3 SunDir = new Vector3(0.5f, 0.8f, -0.4f).normalized;

        private static Dictionary<Map, Texture2D> _terrainCache = new Dictionary<Map, Texture2D>();
        private static Dictionary<Map, Texture2D> _terrainCacheHi = new Dictionary<Map, Texture2D>();
        private static Map[] _lastMapList;
        private static string _lastColorMode;   // 上次着色模式（变化时清空纹理缓存）

        // 虚拟摄像机渲染（beta）
        private static Camera _minimapCam;
        private static Camera _worldmapCam;
        private static RenderTexture _minimapRT;
        private static RenderTexture _worldmapRT;
        private static Texture2D _minimapTex;
        private static Texture2D _worldmapTex;
        private static float _lastMinimapRenderTime;
        private static float _lastWorldmapRenderTime;
        private static int _terrainMask;
        private static int _obstructionMask;
        private static bool _camsReady;

        // TileMap/TMCache 分块渲染
        private static RenderTexture _tileRT;        // 单块 RT
        private static Texture2D _worldmapTiledTex;  // 分块拼接大图
        private static Texture2D _tmCacheTex;        // TMCache 缓存的大图
        private static float _tmCacheCenterX;
        private static float _tmCacheCenterZ;
        private static float _tmCacheZoom;

        // [Beta] 加载物体：反射调用 StrandedWorld.LoadZone 强制加载所有岛屿物体
        private static System.Reflection.MethodInfo _loadZoneMethod;
        private static float _lastLoadAllTime;

        // 临时高精度地形 LOD：VC 渲染前临时调低 heightmapPixelError、拉高 basemapDistance，
        // 渲染完恢复，让 VC 得到和玩家视角一样精细的地形（而不是被 LOD 简化成糊块）。
        private static UnityEngine.Terrain[] _detailTerrains;
        private static float[] _detailOldErrors;
        private static float[] _detailOldDistances;

        public static bool WorldMapOpen
        {
            get { return _worldMapOpen; }
        }

        public static void SetWorldMapOpen(bool open)
        {
            if (_worldMapOpen == open)
            {
                return;
            }
            _worldMapOpen = open;
            if (open)
            {
                _prevCursorVisible = Cursor.visible;
                _prevLockMode = Cursor.lockState;
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
                SetPlayerInputEnabled(false);
                _contextMenuOpen = false;
                _mapCenterInit = false;   // 打开时地图中心重置回玩家位置
                if (Main.Instance != null)
                {
                    _mapZoom = Mathf.Clamp(Main.Instance.MapZoom.Value, 0.5f, 4096f);
                }
            }
            else
            {
                SetPlayerInputEnabled(true);
                // 明确恢复第一人称锁定，而不是恢复到打开前保存的状态——
                // 否则打开大地图前若 LockState 非 Locked，关闭后小地图会一直隐藏（bug）。
                Cursor.visible = false;
                Cursor.lockState = CursorLockMode.Locked;
            }
        }

        public static void Draw()
        {
            if (!Game.State.IsGame())
            {
                return;
            }
            IPlayer player = PlayerRegistry.LocalPlayer;
            if (player == null)
            {
                return;
            }

            EnsureStyles();
            Main m = Main.Instance;
            CheckTerrainCache();

            // [Beta] 加载物体：实时渲染（VC/TileMap/TMCache）模式下周期性强制加载所有岛屿物体
            if (m.IsRealtimeRender && m.LoadObjects.Value && Time.unscaledTime - _lastLoadAllTime > 2f)
            {
                _lastLoadAllTime = Time.unscaledTime;
                LoadAllZonesForRender();
            }

            // 滚轮缩放（大地图打开时，先于绘制处理，避免被消费）
            if (_worldMapOpen)
            {
                Event ev = Event.current;
                if (ev.type == EventType.ScrollWheel)
                {
                    // 滚轮方向反转：向上滚 = 缩小（看全景），向下滚 = 放大（看局部）
                    _mapZoom = Mathf.Clamp(_mapZoom * (ev.delta.y > 0f ? 0.833f : 1.2f), 0.5f, 4096f);
                    ev.Use();
                }
            }

            // 小地图只在"正常游玩、无任何 UI 覆盖层"时显示。
            // 合成菜单(CrafterMenuPresenter)、背包(InventoryMenuPresenter)、暂停菜单(MainMenuPresenter)、
            // 建造(ConstructionObject_BED)、存档(SaveManager)、标签机等 UI 打开时，都会调 SetCursorEnabled(true)
            // 把 CursorUtils.LockState 从 Locked 切到 Confined/None。此时隐藏小地图，避免遮挡边缘 UI。
            bool paused = Beam.UI.MainMenuPresenter.Instance != null && Beam.UI.MainMenuPresenter.Instance.IsGamePaused;
            bool minimapShown = m.MinimapEnabled.Value && !paused && CursorUtils.LockState == CursorLockMode.Locked;
            if (minimapShown)
            {
                if (m.IsRealtimeRender)
                {
                    DrawMinimapVirtual(player, m);
                }
                else
                {
                    DrawMinimap(player, m);
                }
            }
            if (_worldMapOpen)
            {
                if (m.IsRealtimeRender)
                {
                    DrawWorldmapVirtual(player, m);
                }
                else
                {
                    DrawWorldMap(player, m);
                }
            }
        }

        // ==================== 虚拟摄像机渲染（beta） ====================
        private static void EnsureCameras()
        {
            if (_camsReady)
            {
                return;
            }
            _camsReady = true;
            // 注意：不含 TERRAIN_DETAILS（草层）——正交俯视时草 billboard 会渲染成"一条一条"的条纹，
            // 影响地图观感，故从地图渲染中排除。
            // PLAYER 层：用于在地图上同时渲染玩家模型（兼容 MP 联机下其他玩家的位置也能看到）。
            _terrainMask = (1 << Layers.TERRAIN) | (1 << Layers.TERRAIN_OBJECTS) | (1 << Layers.WATER)
                | (1 << Layers.INTERACTIVE_TREES) | (1 << Layers.INTERACTIVE_OBJECTS)
                | (1 << Layers.CONSTRUCTIONS) | (1 << Layers.CONSTRUCTIONS_SMALL) | (1 << Layers.CONSTRUCTIONS_RAFTS)
                | (1 << Layers.PLAYER);
            _obstructionMask = (1 << Layers.CONSTRUCTIONS) | (1 << Layers.CONSTRUCTIONS_SMALL) | (1 << Layers.CONSTRUCTIONS_RAFTS) | (1 << Layers.INTERACTIVE_TREES) | (1 << Layers.INTERACTIVE_OBJECTS);
            _minimapCam = CreateTopDownCamera("SDMap_MinimapCam");
            _worldmapCam = CreateTopDownCamera("SDMap_WorldmapCam");
        }

        // [Beta] 强制加载所有未加载的岛屿物体，使其能被虚拟摄像机渲染。
        // 原版只有玩家附近的岛屿（Zone）才加载物体（PollLoad 依据玩家距离），远处岛屿物体处于未激活状态，
        // 摄像机 cullingMask 即使包含它们也渲染不到。这里反射调用 private StrandedWorld.LoadZone(zone) 逐个加载。
        private static void LoadAllZonesForRender()
        {
            try
            {
                StrandedWorld world = UnityEngine.Object.FindObjectOfType<StrandedWorld>();
                if (world == null)
                {
                    return;
                }
                Zone[] zones = world.Zones;
                if (zones == null)
                {
                    return;
                }
                if (_loadZoneMethod == null)
                {
                    _loadZoneMethod = typeof(StrandedWorld).GetMethod("LoadZone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                }
                if (_loadZoneMethod == null)
                {
                    return;
                }
                for (int i = 0; i < zones.Length; i++)
                {
                    Zone zone = zones[i];
                    if (zone == null || zone.Loading || zone.Loaded)
                    {
                        continue;
                    }
                    // LoadZone 内部直接索引 _zoneSavedObjectsLookup[name]，无数据时会抛异常；用 public GetZoneData 先判空
                    if (world.GetZoneData(zone) == null)
                    {
                        continue;
                    }
                    _loadZoneMethod.Invoke(world, new object[] { zone });
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SDMap] LoadAllZonesForRender failed: " + e.Message);
            }
        }

        private static Camera CreateTopDownCamera(string name)
        {
            GameObject go = new GameObject(name);
            UnityEngine.Object.DontDestroyOnLoad(go);
            Camera cam = go.AddComponent<Camera>();
            // 透视默认 / 正交可选。UseOrthographic 影响所有 VC-based 渲染（VirtualCamera / TileMap / TMCache）：
            //   透视（默认）：画面中心高 LOD + 远端压缩，接近玩家视角；远视时需相机高度 >> farClipPlane，farClipPlane 动态抬到足够大。
            //   正交：高度恒定 4000f 全图均匀精度；远视时不会有"被 farClipPlane 裁光"的副作用。
            // 注意：cam.orthographic 的实际值在 RenderTopDown / RenderTopDownTiled 每次渲染前按配置同步，
            // 这里只是给一个初始值（否则改配置不重建相机、选项不生效）。
            cam.orthographic = false;
            cam.fieldOfView = 60f;
            cam.cullingMask = _terrainMask;
            cam.clearFlags = CameraClearFlags.SolidColor;
            // 未探索/未渲染到的区域直接填成"海"的颜色（DeepSea 深海色），而不是透明或纯色块——
            // 用户要求：未探索岛屿 = 截取一块海来填充，而非透明（透明会露出底层 DrawRect / 变黑）。
            cam.backgroundColor = DeepSea;
            cam.enabled = false; // 手动渲染
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 30000f;   // 大值兜底，RenderTopDown 会按 height 动态收紧
            return cam;
        }

        // 透视俯视高度：要覆盖"半径=orthoSize"的水平视野，相机高度 = orthoSize / tan(FOV/2)
        private static float CameraHeightForOrthoSize(float orthoSize)
        {
            float halfFov = 30f * Mathf.Deg2Rad;   // FOV=60 → 半角 30°
            return orthoSize / Mathf.Tan(halfFov);
        }

        // 临时切换地形 LOD 精度。玩家视角清晰，是因为透视 + 贴地让 Unity 用近处高 LOD；
        // VC 正交相机高空俯视，在 heightmapPixelError=75 下远处地形被激进简化成糊块。
        // 渲染 VC 前临时降到 5（Unity 默认高精度）、basemapDistance 拉高覆盖全图，渲染完立即恢复，
        // 让 VC 采样的这一帧得到和玩家视角一样精细的真实地形几何。
        private static void SetTerrainHighDetail(bool high)
        {
            if (high)
            {
                if (_detailTerrains == null)
                {
                    StrandedWorld world = UnityEngine.Object.FindObjectOfType<StrandedWorld>();
                    if (world != null && world.Zones != null)
                    {
                        List<UnityEngine.Terrain> list = new List<UnityEngine.Terrain>();
                        for (int i = 0; i < world.Zones.Length; i++)
                        {
                            Zone z = world.Zones[i];
                            if (z != null && z.Terrain != null)
                            {
                                list.Add(z.Terrain);
                            }
                        }
                        _detailTerrains = list.ToArray();
                        _detailOldErrors = new float[list.Count];
                        _detailOldDistances = new float[list.Count];
                    }
                }
                if (_detailTerrains == null)
                {
                    return;
                }
                for (int i = 0; i < _detailTerrains.Length; i++)
                {
                    UnityEngine.Terrain t = _detailTerrains[i];
                    if (t == null)
                    {
                        continue;
                    }
                    _detailOldErrors[i] = t.heightmapPixelError;
                    _detailOldDistances[i] = t.basemapDistance;
                    t.heightmapPixelError = 5f;
                    t.basemapDistance = 20000f;
                }
            }
            else
            {
                if (_detailTerrains == null)
                {
                    return;
                }
                for (int i = 0; i < _detailTerrains.Length; i++)
                {
                    UnityEngine.Terrain t = _detailTerrains[i];
                    if (t == null)
                    {
                        continue;
                    }
                    t.heightmapPixelError = _detailOldErrors[i];
                    t.basemapDistance = _detailOldDistances[i];
                }
            }
        }

        // 头顶是否有遮挡（建筑/树等）
        private static bool IsHeadObstructed(IPlayer player)
        {
            Vector3 origin = player.transform.position + Vector3.up * 1.5f;
            RaycastHit hit;
            return Physics.Raycast(origin, Vector3.up, out hit, 100f, _obstructionMask);
        }

        // 渲染地图时，把所有玩家临时切到"第三人称完整模型"（含头），渲染完恢复。
        // 第一人称下玩家头被 SetHeadRenderers(shadowsOnly=true) 隐藏（防遮挡视野），但地图相机是"其他视角"，
        // 应看到完整的玩家模型（含头）。做法：渲染前禁用 FirstPerson 渲染器、启用 ThirdPerson 渲染器（含头），
        // 渲染后按玩家当前 CameraMode 恢复原状（与 Character.PlayerCamera_RenderingCameraChanged 逻辑一致）。
        private static void SetPlayersThirdPersonForMap(bool on)
        {
            IList<IPlayer> players = PlayerRegistry.AllPlayers;
            if (players == null)
            {
                return;
            }
            for (int i = 0; i < players.Count; i++)
            {
                IPlayer p = players[i];
                if (p == null || p.Character == null)
                {
                    continue;
                }
                Character c = p.Character;
                CharacterRenderer fp = c.CharacterFirstPerson;
                CharacterRenderer tp = c.CharacterThirdPerson;
                if (on)
                {
                    // 强制第三人称完整模型可见（含头），隐藏第一人称（避免重影）
                    if (fp != null) { fp.SetRenderers(false); fp.SetHeadRenderers(false, false); fp.SetArmEffectActive(false); }
                    if (tp != null) { tp.SetRenderers(true); tp.SetHeadRenderers(true, false); tp.SetArmEffectActive(true); }
                }
                else
                {
                    // 恢复：按玩家当前相机模式
                    bool first = p.PlayerCamera != null && p.PlayerCamera.CameraMode == PlayerCameraMode.First;
                    if (fp != null) { fp.SetRenderers(first); fp.SetHeadRenderers(first, true); fp.SetArmEffectActive(first); }
                    if (tp != null) { tp.SetRenderers(!first); tp.SetHeadRenderers(!first, false); tp.SetArmEffectActive(!first); }
                }
            }
        }

        // 兼容 MP 的玩家位置标记：本地玩家画白箭头（调用方已处理），其他玩家画彩色圆点 + ID。
        // 用于小/大地图的 Legacy + VirtualCamera 渲染分支。
        // (world2screen) 把世界 XZ 映射成 GUI 坐标（已含 size 缩放与 origin 偏移），传入 Rect 内坐标 (0..size)。
        private static void DrawAllPlayerMarkers(Func<float, float, Vector2> world2screen, float displaySize, bool showCoord)
        {
            if (!Main.Instance.ShowPlayerMarkers.Value)
            {
                return;
            }
            IList<IPlayer> players = PlayerRegistry.AllPlayers;
            if (players == null)
            {
                return;
            }
            IPlayer local = PlayerRegistry.LocalPlayer;
            for (int i = 0; i < players.Count; i++)
            {
                IPlayer p = players[i];
                if (p == null || p == local || p.transform == null)
                {
                    continue;
                }
                Vector3 pos = p.transform.position;
                Vector2 mp = world2screen(pos.x, pos.z);
                if (mp.x < 0f || mp.y < 0f || mp.x > displaySize || mp.y > displaySize)
                {
                    continue;
                }
                Color c = PlayerColorById(p.Id);
                DrawRect(new Rect(mp.x - 4f, mp.y - 4f, 8f, 8f), c);
                DrawRect(new Rect(mp.x - 5f, mp.y - 5f, 10f, 2f), Color.black);
                DrawRect(new Rect(mp.x - 5f, mp.y + 3f, 10f, 2f), Color.black);
                DrawRect(new Rect(mp.x - 5f, mp.y - 5f, 2f, 10f), Color.black);
                DrawRect(new Rect(mp.x + 3f, mp.y - 5f, 2f, 10f), Color.black);
                if (showCoord)
                {
                    GUI.Label(new Rect(mp.x + 6f, mp.y - 7f, 140f, 18f), "P" + p.Id, _labelStyle);
                }
            }
        }

        // 给每个 peer 一个固定的颜色，便于地图上一眼分辨
        private static readonly Color[] _peerColors = new Color[]
        {
            new Color(0.95f, 0.30f, 0.30f, 1f),
            new Color(0.30f, 0.85f, 0.40f, 1f),
            new Color(0.35f, 0.55f, 1f, 1f),
            new Color(1f, 0.85f, 0.30f, 1f),
        };
        private static Color PlayerColorById(int id)
        {
            if (_peerColors == null || _peerColors.Length == 0)
            {
                return Color.white;
            }
            return _peerColors[((id % _peerColors.Length) + _peerColors.Length) % _peerColors.Length];
        }

// 大地图 RT 分辨率随缩放分级：放大越近 RT 越大（更精细）。
// 但 RT 不能远超显示尺寸（mapSize），否则缩小到 mapSize 时插值模糊——
// 上限封顶为 2*mapSize，确保拉近时 RT 与显示尺寸比 ≤2:1，1:1~1:2 显示避免过度缩放糊化。
private static int WorldmapRTForZoom(float zoom, int mapSize)
        {
            int byZoom = zoom >= 16f ? 2048 : (zoom >= 8f ? 1024 : (zoom >= 2f ? 512 : 256));
            int rt = Mathf.Max(byZoom, mapSize);            // 至少 ≥ 显示尺寸（1:1）
            int capped = Mathf.Min(rt, mapSize * 2);        // 至多 2 倍显示尺寸（避免过度缩小）
            return Mathf.Clamp(capped, mapSize, 2048);
        }

        // 用正交/透视摄像机从上方渲染地形，同步回读 Texture2D（节流）。
        // RT 分辨率随 size 动态重建；用同步 ReadPixels 回读（与 RenderTopDownTiled 一致），
        // 保证方向正确 —— AsyncGPUReadback 在 DX 平台会垂直翻转，导致地图/三角标方向反。
        private static void RenderTopDown(Camera cam, Vector3 center, float orthoSize, bool lowLayer, bool highDetail,
            ref RenderTexture rt, ref Texture2D tex, int size)
        {
            if (rt == null || rt.width != size || rt.height != size)
            {
                if (rt != null)
                {
                    rt.Release();
                }
                rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
            }
            if (tex == null || tex.width != size || tex.height != size)
            {
                if (tex != null)
                {
                    UnityEngine.Object.Destroy(tex);
                }
                tex = new Texture2D(size, size, TextureFormat.RGB24, false);
            }
            // 每次渲染前按配置同步正交/透视（否则改配置不重建相机、选项不生效——"正交相机选项没起作用"的根因）
            bool ortho = Main.Instance != null && Main.Instance.UseOrthographic != null && Main.Instance.UseOrthographic.Value;
            cam.orthographic = ortho;
            float height;
            if (lowLayer)
            {
                // "头顶被遮挡"时使用低相机（贴近地表，渲染脚下与近处物体）
                height = 40f;
            }
            else if (ortho)
            {
                height = 4000f;
                cam.orthographicSize = orthoSize;
            }
            else
            {
                height = CameraHeightForOrthoSize(orthoSize);
            }
            // farClipPlane 必须 >= height + 1（否则相机看到的世界全是"超过远裁剪面"，被 clearColor 填满，
            // 这是"拉远后整片被深海色覆盖"的根因）。正交时固定 6000，透视时取大值兜底。
            cam.farClipPlane = ortho ? 6000f : Mathf.Max(8000f, height + 1500f);
            cam.transform.position = new Vector3(center.x, height, center.z);
            cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            cam.targetTexture = rt;
            // 渲染前临时切到高精度地形 LOD，渲染完恢复，让 VC 得到真实精细地形
            SetTerrainHighDetail(highDetail);
            // 渲染前把所有玩家临时切到"第三人称完整模型"（含头），渲染完恢复——
            // 第一人称下玩家头被 shadowsOnly 隐藏（防遮挡），但地图是"其他视角"，应看到完整玩家模型。
            SetPlayersThirdPersonForMap(true);
            cam.Render();
            SetPlayersThirdPersonForMap(false);
            SetTerrainHighDetail(false);

            // 同步读回（与 RenderTopDownTiled 的 ReadPixels 保持一致，方向由 Unity 统一处理）。
            // 之前用 AsyncGPUReadback：在 Windows(DX) 平台读 RT 时 v=0 在顶部，
            // LoadRawTextureData 按"y=0 在底部"解释 → 地图整体垂直翻转（南北镜像），
            // 玩家三角标（不翻转）相对地图反 180° —— "三角标又反了"的根因。
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0f, 0f, size, size), 0, 0);
            RenderTexture.active = prev;
            tex.Apply();
        }

        // TileMap/TMCache：分块渲染拼接，突破单相机 RT 分辨率上限（同步 ReadPixels，采样帧率低时无感）。
        // tiles×tiles 块，每块用相机渲染到 _tileRT，再读到 _worldmapTiledTex 对应区域，等效分辨率 = 单块 × tiles。
        private static void RenderTopDownTiled(Camera cam, Vector3 center, float range, bool lowLayer, string mode, int tiles, int totalSize)
        {
            // 简化为单相机 + 大 RT 渲染（避开分块坐标对齐 bug），仍用 totalSize 作为目标分辨率
            // tiles 参数保留兼容。实际就是把单相机的渲染 RT 提到 totalSize（如 1400），突破旧 1024 上限。
            int size = totalSize;
            float orthoSize = range / 2f;
            // 每次渲染前按配置同步正交/透视（否则改配置不重建相机、选项不生效）
            bool ortho = Main.Instance != null && Main.Instance.UseOrthographic != null && Main.Instance.UseOrthographic.Value;
            cam.orthographic = ortho;
            float height;
            if (lowLayer)
            {
                height = 40f;
            }
            else if (ortho)
            {
                height = 4000f;
                cam.orthographicSize = orthoSize;
            }
            else
            {
                height = CameraHeightForOrthoSize(orthoSize);
            }
            // 远裁剪面动态调整，修复"拉远后整片被深海色覆盖"
            cam.farClipPlane = ortho ? 6000f : Mathf.Max(8000f, height + 1500f);

            // TMCache：中心/缩放几乎未变时复用缓存，跳过重渲染
            if (mode == "TMCache" && _tmCacheTex != null && _tmCacheTex.width == size
                && Mathf.Abs(_tmCacheCenterX - center.x) < range * 0.05f
                && Mathf.Abs(_tmCacheCenterZ - center.z) < range * 0.05f
                && Mathf.Abs(_tmCacheZoom - _mapZoom) < 0.01f)
            {
                _worldmapTiledTex = _tmCacheTex;
                return;
            }

            if (_tileRT == null || _tileRT.width != size)
            {
                if (_tileRT != null)
                {
                    _tileRT.Release();
                }
                _tileRT = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
            }
            if (_worldmapTiledTex == null || _worldmapTiledTex.width != size)
            {
                if (_worldmapTiledTex != null)
                {
                    UnityEngine.Object.Destroy(_worldmapTiledTex);
                }
                _worldmapTiledTex = new Texture2D(size, size, TextureFormat.RGB24, false);
            }

            cam.transform.position = new Vector3(center.x, height, center.z);
            cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            cam.targetTexture = _tileRT;

            SetTerrainHighDetail(true);
            // 渲染前把所有玩家临时切到"第三人称完整模型"（含头），渲染完恢复
            SetPlayersThirdPersonForMap(true);
            cam.Render();
            SetPlayersThirdPersonForMap(false);
            SetTerrainHighDetail(false);

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = _tileRT;
            _worldmapTiledTex.ReadPixels(new Rect(0f, 0f, size, size), 0, 0);
            RenderTexture.active = prev;
            _worldmapTiledTex.Apply();

            // TMCache：缓存本次结果
            if (mode == "TMCache")
            {
                if (_tmCacheTex != null && _tmCacheTex != _worldmapTiledTex)
                {
                    UnityEngine.Object.Destroy(_tmCacheTex);
                }
                _tmCacheTex = _worldmapTiledTex;
                _tmCacheCenterX = center.x;
                _tmCacheCenterZ = center.z;
                _tmCacheZoom = _mapZoom;
            }
        }

        private static void DrawMinimapVirtual(IPlayer player, Main m)
        {
            EnsureCameras();
            int size = Mathf.Clamp(m.MinimapSize.Value, 80, 400);
            float zoom = Mathf.Max(1f, m.MinimapZoom.Value);
            float margin = 12f;
            float coordH = m.ShowCoordinates.Value ? 24f : 0f;

            string pos = m.MinimapPosition.Value;
            bool isLeft = pos.StartsWith("左");
            bool isTop = pos.EndsWith("上");
            float x = isLeft ? margin : Screen.width - size - margin;
            float y = isTop ? margin : Screen.height - size - margin - coordH;

            Rect mapRect = new Rect(x, y, size, size);

            GUI.Box(new Rect(mapRect.x - 2f, mapRect.y - 2f, mapRect.width + 4f, mapRect.height + 4f), GUIContent.none);

            // 节流渲染（采样帧率可配置）
            float interval = 1f / Mathf.Max(1, m.MinimapRenderFPS.Value);
            if (Time.unscaledTime - _lastMinimapRenderTime > interval)
            {
                _lastMinimapRenderTime = Time.unscaledTime;
                bool obstructed = IsHeadObstructed(player);
                Vector3 ppos = player.transform.position;
                RenderTopDown(_minimapCam, ppos, zoom, obstructed, false, ref _minimapRT, ref _minimapTex, Mathf.Clamp(size, 128, 512));
            }

            if (_minimapTex != null)
            {
                GUI.BeginGroup(mapRect);
                GUI.DrawTexture(new Rect(0f, 0f, size, size), _minimapTex, ScaleMode.ScaleAndCrop);
                // 玩家箭头（固定北，朝向 RotationX）
                DrawArrow(new Vector2(size / 2f, size / 2f), player.PlayerCamera.MouseLook.RotationX, size * 0.08f, Color.white);
                // 路径点（固定北，叠加在地图纹理之上）
                for (int i = 0; i < WaypointManager.Waypoints.Count; i++)
                {
                    Waypoint wp = WaypointManager.Waypoints[i];
                    Vector2 mp = WorldToMinimap(wp.X, wp.Z, player, size, zoom);
                    if (mp.x < 0f || mp.y < 0f || mp.x > size || mp.y > size)
                    {
                        continue;
                    }
                    DrawRect(new Rect(mp.x - 3f, mp.y - 3f, 6f, 6f), WaypointManager.GetColor(wp.ColorIndex));
                    if (m.ShowCoordinates.Value)
                    {
                        GUI.Label(new Rect(mp.x + 5f, mp.y - 8f, 160f, 18f),
                            wp.Name + " Y:" + Mathf.RoundToInt(wp.Y), _labelStyle);
                    }
                }
                // 联机其他玩家（兼容 MP）
                DrawAllPlayerMarkers((wx, wz) => WorldToMinimap(wx, wz, player, size, zoom), size, m.ShowCoordinates.Value);
                GUI.EndGroup();
            }

            if (m.ShowCoordinates.Value)
            {
                Vector3 p = player.transform.position;
                float labelY = isTop ? y + size + 4f : y - coordH;
                GUI.Label(new Rect(x, labelY, size, 22f),
                    string.Format("X:{0:F0}  Y:{1:F0}  Z:{2:F0}", p.x, p.y, p.z), _labelStyle);
            }
        }

        private static void DrawWorldmapVirtual(IPlayer player, Main m)
        {
            EnsureCameras();
            int mapSize = Mathf.Clamp(m.MapSize.Value, 240, 1400);
            mapSize = Mathf.Min(mapSize, Screen.width - 30, Screen.height - 260);

            float titleH = 30f;
            float coordH = 22f;
            float listH = 130f;
            float pad = 10f;
            float totalW = mapSize + pad * 2f;
            float totalH = titleH + mapSize + coordH + listH + pad * 2f;

            float frameX = Mathf.Max(5f, (Screen.width - totalW) / 2f);
            float frameY = Mathf.Max(5f, (Screen.height - totalH) / 2f);

            GUI.Box(new Rect(frameX, frameY, totalW, totalH), GUIContent.none);

            GUI.Label(new Rect(frameX + 12f, frameY + 6f, totalW - 80f, 20f), "世界地图", _titleStyle);
            if (GUI.Button(new Rect(frameX + totalW - 34f, frameY + 5f, 28f, 20f), "X", _buttonStyle))
            {
                SetWorldMapOpen(false);
            }

            Rect mapRect = new Rect(frameX + pad, frameY + titleH, mapSize, mapSize);

            Vector3 ppos = player.transform.position;
            if (!_mapCenterInit)
            {
                _mapCenterX = ppos.x;
                _mapCenterZ = ppos.z;
                _mapCenterInit = true;
            }

            // 节流渲染（采样帧率可配置）
            float interval = 1f / Mathf.Max(1, m.WorldmapRenderFPS.Value);
            string wmMode = m.ActiveColorMode;
            bool tiled = wmMode == "TileMap" || wmMode == "TMCache";
            if (Time.unscaledTime - _lastWorldmapRenderTime > interval)
            {
                _lastWorldmapRenderTime = Time.unscaledTime;
                bool obstructed = IsHeadObstructed(player);
                float range = WorldRange / _mapZoom;
                Vector3 center = new Vector3(_mapCenterX, 0f, _mapCenterZ);
                if (tiled)
                {
                    RenderTopDownTiled(_worldmapCam, center, range, obstructed, wmMode, 2, mapSize * 2);
                }
                else
                {
                    RenderTopDown(_worldmapCam, center, range / 2f, obstructed, true, ref _worldmapRT, ref _worldmapTex, WorldmapRTForZoom(_mapZoom, mapSize));
                }
            }

            Texture2D mapTex = tiled ? _worldmapTiledTex : _worldmapTex;
            if (mapTex != null)
            {
                GUI.BeginGroup(mapRect);
                GUI.DrawTexture(new Rect(0f, 0f, mapSize, mapSize), mapTex, ScaleMode.ScaleAndCrop);
                // 玩家箭头（相对地图中心）
                float range = WorldRange / _mapZoom;
                float scale = mapSize / range;
                Vector2 playerMp = WorldToMapCenter(ppos.x, ppos.z, _mapCenterX, _mapCenterZ, scale, mapSize);
                DrawArrow(playerMp, player.PlayerCamera.MouseLook.RotationX, 24f, Color.white);
                // 路径点（叠加在地图纹理之上）
                for (int i = 0; i < WaypointManager.Waypoints.Count; i++)
                {
                    Waypoint wp = WaypointManager.Waypoints[i];
                    Vector2 mp = WorldToMapCenter(wp.X, wp.Z, _mapCenterX, _mapCenterZ, scale, mapSize);
                    if (mp.x < 0f || mp.y < 0f || mp.x > mapSize || mp.y > mapSize)
                    {
                        continue;
                    }
                    DrawRect(new Rect(mp.x - 3f, mp.y - 3f, 6f, 6f), WaypointManager.GetColor(wp.ColorIndex));
                    GUI.Label(new Rect(mp.x + 5f, mp.y - 8f, 240f, 18f),
                        wp.Name + " Y:" + Mathf.RoundToInt(wp.Y), _labelStyle);
                }
                // 联机其他玩家（兼容 MP）
                DrawAllPlayerMarkers((wx, wz) => WorldToMapCenter(wx, wz, _mapCenterX, _mapCenterZ, scale, mapSize), mapSize, true);
                GUI.EndGroup();
            }

            GUI.Label(new Rect(frameX + pad, frameY + titleH + mapSize + 2f, totalW - pad * 2f, coordH),
                string.Format("X:{0:F0}  Y:{1:F0}  Z:{2:F0}", ppos.x, ppos.y, ppos.z), _labelStyle);

            DrawWaypointList(new Rect(frameX + pad, frameY + titleH + mapSize + coordH + 2f, mapSize, listH), m);

            DrawContextMenu();
            HandleMapClick(mapRect, mapSize, WorldRange / _mapZoom > 0f ? mapSize / (WorldRange / _mapZoom) : 1f, ppos, m);
        }

        // ==================== 小地图 ====================
        private static void DrawMinimap(IPlayer player, Main m)
        {
            int size = Mathf.Clamp(m.MinimapSize.Value, 80, 400);
            float zoom = Mathf.Max(1f, m.MinimapZoom.Value);
            float margin = 12f;
            float coordH = m.ShowCoordinates.Value ? 24f : 0f;

            string pos = m.MinimapPosition.Value;
            bool isLeft = pos.StartsWith("左");
            bool isTop = pos.EndsWith("上");
            float x = isLeft ? margin : Screen.width - size - margin;
            float y = isTop ? margin : Screen.height - size - margin - coordH;

            Rect mapRect = new Rect(x, y, size, size);
            float scale = (size / 2f) / zoom;
            float opacity = m.MinimapOpacity.Value;

            // 小地图边框
            GUI.Box(new Rect(mapRect.x - 2f, mapRect.y - 2f, mapRect.width + 4f, mapRect.height + 4f), GUIContent.none);

            // 裁剪：超出小地图框的内容不绘制
            GUI.BeginGroup(mapRect);

            DrawRect(new Rect(0f, 0f, size, size), new Color(DeepSea.r, DeepSea.g, DeepSea.b, 1f));

            Vector2[] islands = World.GenerationZonePositons;
            Map[] maps = World.MapList;
            if (islands != null)
            {
                for (int i = 0; i < islands.Length; i++)
                {
                    Vector2 mp = WorldToMinimap(islands[i].x, islands[i].y, player, size, zoom);
                    Map map = (maps != null && i < maps.Length) ? maps[i] : null;
                    Texture2D icon = map != null ? GetTerrainTexture(map, false, i) : null;
                    if (icon != null)
                    {
                        float dia = IslandDiameter * scale;
                        GUI.DrawTexture(new Rect(mp.x - dia / 2f, mp.y - dia / 2f, dia, dia), icon);
                    }
                    else
                    {
                        DrawRect(new Rect(mp.x - 2f, mp.y - 2f, 4f, 4f), new Color(0.35f, 0.75f, 0.4f, 0.9f));
                    }
                }
            }

            for (int i = 0; i < WaypointManager.Waypoints.Count; i++)
            {
                Waypoint wp = WaypointManager.Waypoints[i];
                Vector2 mp = WorldToMinimap(wp.X, wp.Z, player, size, zoom);
                DrawRect(new Rect(mp.x - 3f, mp.y - 3f, 6f, 6f), WaypointManager.GetColor(wp.ColorIndex));
                if (m.ShowCoordinates.Value)
                {
                    GUI.Label(new Rect(mp.x + 5f, mp.y - 8f, 160f, 18f),
                        wp.Name + " Y:" + Mathf.RoundToInt(wp.Y), _labelStyle);
                }
            }

            // 玩家箭头（固定北朝上，箭头指向实际朝向 RotationX）
            DrawArrow(new Vector2(size / 2f, size / 2f), player.PlayerCamera.MouseLook.RotationX, size * 0.08f, Color.white);

            // 联机其他玩家（兼容 MP）
            DrawAllPlayerMarkers((wx, wz) => WorldToMinimap(wx, wz, player, size, zoom), size, m.ShowCoordinates.Value);

            GUI.EndGroup();

            if (m.ShowCoordinates.Value)
            {
                Vector3 p = player.transform.position;
                float labelY = isTop ? y + size + 4f : y - coordH;
                GUI.Label(new Rect(x, labelY, size, 22f),
                    string.Format("X:{0:F0}  Y:{1:F0}  Z:{2:F0}", p.x, p.y, p.z), _labelStyle);
            }
        }

        // ==================== 大地图 ====================
        private static void DrawWorldMap(IPlayer player, Main m)
        {
            int mapSize = Mathf.Clamp(m.MapSize.Value, 240, 1400);
            mapSize = Mathf.Min(mapSize, Screen.width - 30, Screen.height - 260);

            float titleH = 30f;
            float coordH = 22f;
            float listH = 130f;
            float pad = 10f;
            float totalW = mapSize + pad * 2f;
            float totalH = titleH + mapSize + coordH + listH + pad * 2f;

            float frameX = Mathf.Max(5f, (Screen.width - totalW) / 2f);
            float frameY = Mathf.Max(5f, (Screen.height - totalH) / 2f);

            // 大框（包住标题 + 地图 + 坐标 + 列表）
            Rect frame = new Rect(frameX, frameY, totalW, totalH);
            GUI.Box(frame, GUIContent.none);

            // 标题栏
            GUI.Label(new Rect(frameX + 12f, frameY + 6f, totalW - 80f, 20f), "世界地图", _titleStyle);
            if (GUI.Button(new Rect(frameX + totalW - 34f, frameY + 5f, 28f, 20f), "X", _buttonStyle))
            {
                SetWorldMapOpen(false);
            }

            // 地图区（裁剪：超出框的内容不绘制）
            Rect mapRect = new Rect(frameX + pad, frameY + titleH, mapSize, mapSize);

            Vector3 ppos = player.transform.position;
            if (!_mapCenterInit)
            {
                _mapCenterX = ppos.x;
                _mapCenterZ = ppos.z;
                _mapCenterInit = true;
            }
            float range = WorldRange / _mapZoom;
            float scale = mapSize / range;
            // 放大（岛屿显示尺寸大）时用高分辨率纹理，实现 Legacy 渲染的"放大精细"
            bool hiRes = scale >= 0.5f;
            // 拉近（放大，像素分明）用 Point，拉远（缩小，平滑）用 Bilinear
            FilterMode fm = scale >= 1f ? FilterMode.Point : FilterMode.Bilinear;

            GUI.BeginGroup(mapRect);

            DrawRect(new Rect(0f, 0f, mapSize, mapSize), new Color(DeepSea.r, DeepSea.g, DeepSea.b, 1f));

            Vector2[] islands = World.GenerationZonePositons;
            Map[] maps = World.MapList;
            if (islands != null)
            {
                for (int i = 0; i < islands.Length; i++)
                {
                    Vector2 mp = WorldToMapCenter(islands[i].x, islands[i].y, _mapCenterX, _mapCenterZ, scale, mapSize);
                    Map map = (maps != null && i < maps.Length) ? maps[i] : null;
                    Texture2D icon = map != null ? GetTerrainTexture(map, hiRes, i) : null;
                    if (icon != null)
                    {
                        icon.filterMode = fm;
                        float dia = IslandDiameter * scale;
                        GUI.DrawTexture(new Rect(mp.x - dia / 2f, mp.y - dia / 2f, dia, dia), icon);
                    }
                    else
                    {
                        DrawRect(new Rect(mp.x - 3f, mp.y - 3f, 6f, 6f), new Color(0.35f, 0.75f, 0.4f, 0.9f));
                    }
                }
            }

            for (int i = 0; i < WaypointManager.Waypoints.Count; i++)
            {
                Waypoint wp = WaypointManager.Waypoints[i];
                Vector2 mp = WorldToMapCenter(wp.X, wp.Z, _mapCenterX, _mapCenterZ, scale, mapSize);
                DrawRect(new Rect(mp.x - 3f, mp.y - 3f, 6f, 6f), WaypointManager.GetColor(wp.ColorIndex));
                GUI.Label(new Rect(mp.x + 5f, mp.y - 8f, 240f, 18f),
                    wp.Name + " Y:" + Mathf.RoundToInt(wp.Y), _labelStyle);
            }

            // 玩家（chevron 箭头，朝向 RotationX）
            Vector2 playerMp = WorldToMapCenter(ppos.x, ppos.z, _mapCenterX, _mapCenterZ, scale, mapSize);
            DrawArrow(playerMp, player.PlayerCamera.MouseLook.RotationX, 24f, Color.white);

            // 联机其他玩家（兼容 MP）
            DrawAllPlayerMarkers((wx, wz) => WorldToMapCenter(wx, wz, _mapCenterX, _mapCenterZ, scale, mapSize), mapSize, true);

            GUI.EndGroup();

            // 坐标
            GUI.Label(new Rect(frameX + pad, frameY + titleH + mapSize + 2f, totalW - pad * 2f, coordH),
                string.Format("X:{0:F0}  Y:{1:F0}  Z:{2:F0}", ppos.x, ppos.y, ppos.z), _labelStyle);

            // 标记点列表
            DrawWaypointList(new Rect(frameX + pad, frameY + titleH + mapSize + coordH + 2f, mapSize, listH), m);

            // 右键菜单（先绘制，让菜单按钮优先消费点击）
            DrawContextMenu();

            // 点击交互（左键点标记点传送 / 右键弹菜单）
            HandleMapClick(mapRect, mapSize, scale, ppos, m);
        }

        private static void DrawWaypointList(Rect area, Main m)
        {
            GUILayout.BeginArea(area);
            if (WaypointManager.Waypoints.Count == 0)
            {
                GUILayout.Label("暂无标记点（在地图上右键添加）", _labelStyle);
            }
            else
            {
                _scroll = GUILayout.BeginScrollView(_scroll);
                for (int i = 0; i < WaypointManager.Waypoints.Count; i++)
                {
                    Waypoint wp = WaypointManager.Waypoints[i];
                    GUILayout.BeginHorizontal();

                    // 颜色块（点击循环切换颜色）
                    Color wpColor = WaypointManager.GetColor(wp.ColorIndex);
                    GUI.backgroundColor = wpColor;
                    if (GUILayout.Button("", _buttonStyle, GUILayout.Width(24f)))
                    {
                        WaypointManager.CycleColor(i);
                    }
                    GUI.backgroundColor = Color.white;

                    // 名字（改名模式下显示输入框）
                    if (_renamingIndex == i)
                    {
                        _renameText = GUILayout.TextField(_renameText, _inputStyle, GUILayout.Width(170f));
                        if (GUILayout.Button("确认", _buttonStyle, GUILayout.Width(48f)))
                        {
                            WaypointManager.Rename(i, _renameText);
                            _renamingIndex = -1;
                            _renameText = "";
                        }
                        if (GUILayout.Button("取消", _buttonStyle, GUILayout.Width(48f)))
                        {
                            _renamingIndex = -1;
                            _renameText = "";
                        }
                    }
                    else
                    {
                        GUILayout.Label((i + 1) + ". " + wp.Name, _labelStyle, GUILayout.Width(150f));
                        GUILayout.Label(string.Format("({0:F0}, {1:F0})", wp.X, wp.Z), _labelStyle, GUILayout.Width(110f));
                        if (m.TeleportEnabled.Value && GUILayout.Button("传送", _buttonStyle, GUILayout.Width(48f)))
                        {
                            TeleportTo(wp.X, wp.Z);
                        }
                        if (GUILayout.Button("分享", _buttonStyle, GUILayout.Width(48f)))
                        {
                            WaypointManager.Share(i);
                        }
                        if (GUILayout.Button("改名", _buttonStyle, GUILayout.Width(48f)))
                        {
                            _renamingIndex = i;
                            _renameText = wp.Name;
                        }
                        if (GUILayout.Button("删除", _buttonStyle, GUILayout.Width(48f)))
                        {
                            WaypointManager.Remove(i);
                        }
                    }

                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
            }
            GUILayout.EndArea();
        }

        private static void HandleMapClick(Rect mapRect, int mapSize, float scale, Vector3 playerPos, Main m)
        {
            Event e = Event.current;
            if (!mapRect.Contains(e.mousePosition))
            {
                return;
            }
            if (e.type == EventType.MouseDown && e.button == 1)
            {
                // 右键：弹出菜单
                Vector2 local = e.mousePosition - new Vector2(mapRect.x, mapRect.y);
                float half = mapSize / 2f;
                _contextWorldX = _mapCenterX + (local.x - half) / scale;
                _contextWorldZ = _mapCenterZ - (local.y - half) / scale;
                _contextScreenPos = e.mousePosition;
                _contextMenuOpen = true;
                e.Use();
                return;
            }
            if (e.type == EventType.MouseDown && e.button == 0)
            {
                // 左键按下：开始平移（pan）
                _panning = true;
                _panStartMouse = e.mousePosition;
                _panStartCenterX = _mapCenterX;
                _panStartCenterZ = _mapCenterZ;
                e.Use();
                return;
            }
            if (_panning && e.type == EventType.MouseDrag && e.button == 0)
            {
                // 左键拖拽：平移地图
                Vector2 delta = e.mousePosition - _panStartMouse;
                _mapCenterX = _panStartCenterX - delta.x / scale;
                _mapCenterZ = _panStartCenterZ + delta.y / scale;
                e.Use();
                return;
            }
            if (_panning && e.type == EventType.MouseUp && e.button == 0)
            {
                _panning = false;
                Vector2 delta = e.mousePosition - _panStartMouse;
                if (_contextMenuOpen)
                {
                    _contextMenuOpen = false;
                    e.Use();
                    return;
                }
                // 未发生拖拽（点击）→ 点标记点传送
                if (delta.sqrMagnitude < 25f && m.TeleportEnabled.Value)
                {
                    Vector2 local = e.mousePosition - new Vector2(mapRect.x, mapRect.y);
                    for (int i = 0; i < WaypointManager.Waypoints.Count; i++)
                    {
                        Waypoint wp = WaypointManager.Waypoints[i];
                        Vector2 mp = WorldToMapCenter(wp.X, wp.Z, _mapCenterX, _mapCenterZ, scale, mapSize);
                        if (Vector2.Distance(mp, local) < 14f)
                        {
                            TeleportTo(wp.X, wp.Z);
                            break;
                        }
                    }
                }
                e.Use();
            }
        }

        private static void DrawContextMenu()
        {
            if (!_contextMenuOpen)
            {
                return;
            }
            Rect menu = new Rect(_contextScreenPos.x, _contextScreenPos.y, 150f, 64f);
            GUI.Box(menu, GUIContent.none);

            if (GUI.Button(new Rect(menu.x + 6f, menu.y + 6f, menu.width - 12f, 24f), "添加传送点", _buttonStyle))
            {
                WaypointManager.Add("标记点 " + (WaypointManager.Waypoints.Count + 1), _contextWorldX, _contextWorldZ);
                _contextMenuOpen = false;
            }
            if (GUI.Button(new Rect(menu.x + 6f, menu.y + 34f, menu.width - 12f, 24f), "取消", _buttonStyle))
            {
                _contextMenuOpen = false;
            }
        }

        // ==================== 坐标映射 ====================
        // 固定北朝上：+X 右、+Z 上
        private static Vector2 WorldToMinimap(float wx, float wz, IPlayer player, float size, float zoom)
        {
            Vector3 p = player.transform.position;
            float dx = wx - p.x;
            float dz = wz - p.z;
            float scale = (size / 2f) / zoom;
            float center = size / 2f;
            return new Vector2(center + dx * scale, center - dz * scale);
        }

        private static Vector2 WorldToMapCenter(float wx, float wz, float playerX, float playerZ, float scale, float winSize)
        {
            float half = winSize / 2f;
            float mx = half + (wx - playerX) * scale;
            float my = half - (wz - playerZ) * scale;
            return new Vector2(mx, my);
        }

        // ==================== 彩色地貌纹理 ====================
        private static void CheckTerrainCache()
        {
            Map[] maps = World.MapList;
            string mode = Main.Instance != null ? Main.Instance.ActiveColorMode : null;
            if (maps != _lastMapList || mode != _lastColorMode)
            {
                _lastMapList = maps;
                _lastColorMode = mode;
                _terrainCache.Clear();
                _terrainCacheHi.Clear();
                _soilMapCache.Clear();
                _detailTerrains = null;   // 世界切换后 Terrain 引用失效，重建
            }
        }

        private static Texture2D GetTerrainTexture(Map map, bool hiRes, int index)
        {
            Dictionary<Map, Texture2D> cache = hiRes ? _terrainCacheHi : _terrainCache;
            int dstSize = hiRes ? TerrainTexSizeHi : TerrainTexSize;

            Texture2D tex;
            if (cache.TryGetValue(map, out tex))
            {
                return tex;
            }

            string mode = Main.Instance != null ? Main.Instance.ActiveColorMode : "Legacy";
            bool soil = mode == "SoilMap";
            float[,] h = soil ? GetSoilMapData(index) : map.HeightmapData;
            if (h == null)
            {
                h = map.HeightmapData;
                soil = false;
            }
            if (h == null)
            {
                return null;
            }

            int srcW = h.GetLength(0);
            int srcH = h.GetLength(1);
            int dstW = dstSize;
            int dstH = dstSize;

            tex = new Texture2D(dstW, dstH, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;

            for (int y = 0; y < dstH; y++)
            {
                int si = (int)((float)y / (dstH - 1) * (srcH - 1));
                for (int x = 0; x < dstW; x++)
                {
                    int sj = (int)((float)x / (dstW - 1) * (srcW - 1));
                    if (soil)
                    {
                        tex.SetPixel(x, y, SoilMapColor(h[sj, si]));
                        continue;
                    }
                    // 高度梯度（法线光照/坡度着色用）
                    int sx0 = sj > 0 ? sj - 1 : 0;
                    int sx1 = sj < srcW - 1 ? sj + 1 : srcW - 1;
                    int sz0 = si > 0 ? si - 1 : 0;
                    int sz1 = si < srcH - 1 ? si + 1 : srcH - 1;
                    float gx = h[sx1, si] - h[sx0, si];
                    float gz = h[sj, sz1] - h[sj, sz0];
                    tex.SetPixel(x, y, ColorizeTerrain(h[sj, si], gx, gz, mode));
                }
            }
            tex.Apply();
            cache[map] = tex;
            return tex;
        }

        // 土壤分布数据（用 Zone 的 seed/biome 重新生成，缓存）
        private static Dictionary<int, float[,]> _soilMapCache = new Dictionary<int, float[,]>();

        private static float[,] GetSoilMapData(int index)
        {
            float[,] cached;
            if (_soilMapCache.TryGetValue(index, out cached))
            {
                return cached;
            }
            StrandedWorld world = UnityEngine.Object.FindObjectOfType<StrandedWorld>();
            if (world == null || world.Zones == null || index < 0 || index >= world.Zones.Length)
            {
                return null;
            }
            Zone zone = world.Zones[index];
            if (zone == null)
            {
                return null;
            }
            try
            {
                float[,] soil = WorldTools.GENERATE_ISLAND_HEIGHTMAP(zone.Seed, zone.Biome, true);
                _soilMapCache[index] = soil;
                return soil;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SDMap] soilmap gen failed: " + e.Message);
                return null;
            }
        }

        // 土壤分布着色：低值=沙/干，中值=草/灌，高值=密林/岩
        private static Color SoilMapColor(float v)
        {
            if (v <= 0f)
            {
                return new Color(0f, 0f, 0f, 0f);   // 无效（海）
            }
            if (v < 0.3f)
            {
                return Color.Lerp(new Color(0.82f, 0.74f, 0.52f, 1f), new Color(0.60f, 0.66f, 0.38f, 1f), v / 0.3f);
            }
            if (v < 0.7f)
            {
                return Color.Lerp(new Color(0.40f, 0.60f, 0.30f, 1f), new Color(0.25f, 0.48f, 0.22f, 1f), (v - 0.3f) / 0.4f);
            }
            return Color.Lerp(new Color(0.25f, 0.48f, 0.22f, 1f), new Color(0.52f, 0.46f, 0.38f, 1f), Mathf.Clamp01((v - 0.7f) / 0.3f));
        }

        // 按着色模式给地形上色（海区域统一透明）
        private static Color ColorizeTerrain(float h, float gx, float gz, string mode)
        {
            // 海平面约 0.667，海区域透明（所有模式统一）
            if (h < 0.667f)
            {
                return new Color(0f, 0f, 0f, 0f);
            }
            switch (mode)
            {
                case "Nlight":   // 统一绿色基底 + 法线光照
                    return ApplyLighting(new Color(0.36f, 0.62f, 0.30f, 1f), gx, gz);
                case "Steepness": // 坡度着色
                    return SteepnessColor(gx, gz);
                case "NLLayer":   // 分层设色 + 法线光照
                    return ApplyLighting(HeightToColor(h), gx, gz);
                case "Shader":    // 灰色高度 + 法线光照（光照浮雕质感，gray 起步 0.3 避免低海拔全黑）
                    {
                        float gray = Mathf.Clamp01(0.30f + (h - 0.667f) * 1.8f);
                        return ApplyLighting(new Color(gray, gray, gray, 1f), gx, gz);
                    }
                default:          // Legacy / SoilMap / TileMap / TMCache（占位）
                    return HeightToColor(h);
            }
        }

        // 法线光照：高度梯度 → 近似法线，叠太阳光，让山脊有明暗立体感
        private static Color ApplyLighting(Color baseColor, float gx, float gz)
        {
            float strength = 20f;   // 梯度放大系数（经验值，让高度图微小的梯度映射到明显坡度）
            Vector3 n = new Vector3(-gx * strength, 1f, -gz * strength).normalized;
            float light = Mathf.Max(0f, Vector3.Dot(n, SunDir));
            float ambient = 0.35f;
            float factor = ambient + (1f - ambient) * light;
            return new Color(baseColor.r * factor, baseColor.g * factor, baseColor.b * factor, baseColor.a);
        }

        // 坡度着色：缓坡绿 → 陡坡棕红
        private static Color SteepnessColor(float gx, float gz)
        {
            float steep = Mathf.Sqrt(gx * gx + gz * gz);
            return Color.Lerp(new Color(0.35f, 0.68f, 0.32f, 1f), new Color(0.72f, 0.36f, 0.20f, 1f), Mathf.Clamp01(steep * 6f));
        }

        private static Color HeightToColor(float h)
        {
            // 海平面约 0.667（num*150-100<0 临界），海区域透明，只绘制陆地，
            // 避免深海色方块覆盖相邻岛屿/背景（背景已是大海色）。
            if (h < 0.667f)
            {
                return new Color(0f, 0f, 0f, 0f);
            }
            if (h < 0.69f)
            {
                float t = (h - 0.667f) / 0.023f;
                return Color.Lerp(new Color(0.87f, 0.82f, 0.55f, 1f), new Color(0.90f, 0.86f, 0.60f, 1f), t);
            }
            if (h < 0.78f)
            {
                float t = (h - 0.69f) / 0.09f;
                return Color.Lerp(new Color(0.36f, 0.66f, 0.32f, 1f), new Color(0.27f, 0.52f, 0.25f, 1f), t);
            }
            if (h < 0.90f)
            {
                float t = (h - 0.78f) / 0.12f;
                return Color.Lerp(new Color(0.55f, 0.50f, 0.40f, 1f), new Color(0.62f, 0.58f, 0.50f, 1f), t);
            }
            float t2 = (h - 0.90f) / 0.10f;
            return Color.Lerp(new Color(0.68f, 0.66f, 0.63f, 1f), Color.white, Mathf.Clamp01(t2));
        }

        // ==================== 传送 ====================
        public static void TeleportTo(float x, float z)
        {
            IPlayer p = PlayerRegistry.LocalPlayer;
            if (p == null)
            {
                return;
            }
            Vector3 target = new Vector3(x, 25f, z);
            CharacterController cc = p.Movement.CharacterController;
            if (cc != null)
            {
                cc.enabled = false;
            }
            p.transform.position = target;
            if (cc != null)
            {
                cc.enabled = true;
            }
            p.Movement.falling = false;
            p.Movement.grounded = false;
            Debug.Log("[SDMap] teleported to (" + x + ", " + z + ")");
        }

        // ==================== 输入屏蔽 ====================
        private static void SetPlayerInputEnabled(bool enabled)
        {
            try
            {
                Rewired.Player player = ReInput.players.GetPlayer(0);
                if (player != null)
                {
                    // 只禁用"Default"分类（游戏玩法移动/视角），保留 UI 与暂停映射，
                    // 避免 SetAllMapsEnabled 把 ESC 暂停等一起禁掉。
                    player.controllers.maps.SetMapsEnabled(enabled, "Default");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SDMap] input toggle failed: " + e.Message);
            }
        }

        // ==================== 绘制辅助 ====================
        private static void DrawRect(Rect r, Color c)
        {
            GUI.color = c;
            GUI.DrawTexture(r, GetWhiteTex());
            GUI.color = Color.white;
        }

        private static void DrawArrow(Vector2 center, float yawDeg, float length, Color color)
        {
            // 长:宽 = 4:3 箭头，length 为箭头长度（前进方向），宽度 = length * 3/4
            float width = length * 0.75f;
            // 用 RotateAroundPivot 叠加旋转到当前 GUI 矩阵，避免在 BeginGroup 内
            // 直接覆盖 GUI.matrix 导致箭头坐标跑飞（裁剪矩阵被替换）。
            Matrix4x4 old = GUI.matrix;
            GUIUtility.RotateAroundPivot(yawDeg, center);
            GUI.color = color;
            GUI.DrawTexture(new Rect(center.x - width / 2f, center.y - length / 2f, width, length), GetArrowTex());
            GUI.color = Color.white;
            GUI.matrix = old;
        }

        private static Texture2D GetWhiteTex()
        {
            if (_whiteTex == null)
            {
                _whiteTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _whiteTex.SetPixel(0, 0, Color.white);
                _whiteTex.Apply();
            }
            return _whiteTex;
        }

        // 实心三角形箭头（尖端朝上，无尾部凹槽），长:宽 = 4:3，带黑色描边
        private static Texture2D GetArrowTex()
        {
            if (_arrowTex == null)
            {
                int sH = 80;   // 高（长）
                int sW = 60;   // 宽（长:宽 = 4:3）
                _arrowTex = new Texture2D(sW, sH, TextureFormat.RGBA32, false);
                _arrowTex.filterMode = FilterMode.Bilinear;
                Color[] px = new Color[sW * sH];
                Color clear = Color.clear;
                Color white = Color.white;
                Color black = new Color(0f, 0f, 0f, 1f);
                float cx = (sW - 1) / 2f;

                // 第一遍：标记形状（实心三角形，无凹槽）
                bool[,] shape = new bool[sW, sH];
                for (int y = 0; y < sH; y++)
                {
                    // Texture2D 的 y=0 是底部，GUI.DrawTexture 把底部显示在屏幕下方。
                    // 让 y=0 为宽底（尾部）、y=sH-1 为尖端（顶部），尖端才朝上。
                    float t = 1f - (float)y / (sH - 1);
                    float halfW = (sW / 2f) * t;
                    for (int x = 0; x < sW; x++)
                    {
                        float dist = Mathf.Abs(x - cx);
                        shape[x, y] = dist <= halfW;
                    }
                }

                // 第二遍：描边（边缘黑色）+ 填充（白色）
                for (int y = 0; y < sH; y++)
                {
                    for (int x = 0; x < sW; x++)
                    {
                        if (!shape[x, y])
                        {
                            px[y * sW + x] = clear;
                            continue;
                        }
                        // 8 邻域边缘检测：与形状外相邻的像素 = 描边
                        bool edge = false;
                        for (int dy = -1; dy <= 1 && !edge; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0)
                                {
                                    continue;
                                }
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || nx >= sW || ny < 0 || ny >= sH)
                                {
                                    edge = true;
                                    break;
                                }
                                if (!shape[nx, ny])
                                {
                                    edge = true;
                                    break;
                                }
                            }
                        }
                        px[y * sW + x] = edge ? black : white;
                    }
                }
                _arrowTex.SetPixels(px);
                _arrowTex.Apply();
            }
            return _arrowTex;
        }

        private static void EnsureStyles()
        {
            if (_fontReady)
            {
                return;
            }
            _fontReady = true;

            try
            {
                _font = Font.CreateDynamicFontFromOSFont(new string[] { "Microsoft YaHei", "SimHei", "Arial" }, 14);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SDMap] create font failed: " + e.Message);
            }

            // label 类用空样式（避免克隆 GUI.skin.label 的 lineHeight 导致文字下半截被裁）
            _labelStyle = new GUIStyle();
            _labelStyle.font = _font;
            _labelStyle.fontSize = 14;
            _labelStyle.fixedHeight = 22f;
            _labelStyle.alignment = TextAnchor.MiddleLeft;
            _labelStyle.clipping = TextClipping.Clip;
            _labelStyle.normal.textColor = Color.white;

            _titleStyle = new GUIStyle();
            _titleStyle.font = _font;
            _titleStyle.fontSize = 16;
            _titleStyle.fixedHeight = 24f;
            _titleStyle.fontStyle = FontStyle.Bold;
            _titleStyle.alignment = TextAnchor.MiddleLeft;
            _titleStyle.clipping = TextClipping.Clip;
            _titleStyle.normal.textColor = new Color(1f, 0.9f, 0.4f, 1f);

            _buttonStyle = new GUIStyle(GUI.skin.button);
            _buttonStyle.font = _font;
            _buttonStyle.fontSize = 13;
            _buttonStyle.fixedHeight = 22f;
            _buttonStyle.alignment = TextAnchor.MiddleCenter;

            _inputStyle = new GUIStyle(GUI.skin.textField);
            _inputStyle.font = _font;
            _inputStyle.fontSize = 13;
            _inputStyle.fixedHeight = 22f;
            _inputStyle.alignment = TextAnchor.MiddleLeft;
        }
    }
}
