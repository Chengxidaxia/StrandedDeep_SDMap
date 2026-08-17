using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SDMap
{
    [BepInPlugin("com.chengxidaxia.sdmap", "SDMap", "1.0.0")]
    public class Main : BaseUnityPlugin
    {
        public static Main Instance { get; private set; }

        private Harmony _harmony;

        // ---------- 快捷键 ----------
        public ConfigEntry<KeyCode> ToggleMapKey;

        // ---------- 小地图 ----------
        public ConfigEntry<bool> MinimapEnabled;
        public ConfigEntry<int> MinimapSize;
        public ConfigEntry<float> MinimapZoom;
        public ConfigEntry<string> MinimapPosition;
        public ConfigEntry<float> MinimapOpacity;
        public ConfigEntry<bool> ShowCoordinates;
        public ConfigEntry<int> MinimapRenderFPS;   // 小地图（虚拟摄像机渲染）采样帧率

        // ---------- 大地图 ----------
        public ConfigEntry<int> MapSize;
        public ConfigEntry<float> MapZoom;   // 默认缩放（1 = 全图范围）
        public ConfigEntry<int> WorldmapRenderFPS;   // 大地图（虚拟摄像机渲染）采样帧率

        // ---------- 渲染 ----------
        public ConfigEntry<string> RenderMode;         // 普通渲染（单选：Legacy / VirtualCamera / Shader）
        public ConfigEntry<bool> LoadObjects;          // 允许虚拟摄像机加载物体
        public ConfigEntry<bool> UseOrthographic;      // 是否使用正交相机（影响所有 VC-based 渲染：VirtualCamera / TileMap / TMCache）；默认 false = 透视
        public ConfigEntry<bool> ShowPlayerMarkers;    // 渲染所有玩家位置（兼容 MP）；正交/透视都生效
        public ConfigEntry<bool> OtherRenderEnabled;   // 启用其他渲染（启用时锁定普通渲染）
        public ConfigEntry<string> OtherRenderMode;    // 其他渲染方式（下拉）

        public const string RenderLegacy = "Legacy";
        public const string RenderVirtualCamera = "VirtualCamera";
        public const string RenderShader = "Shader";

        public const string OtherSoilMap = "SoilMap";
        public const string OtherNlight = "Nlight";
        public const string OtherSteepness = "Steepness";
        public const string OtherNLLayer = "NLLayer";
        public const string OtherTileMap = "TileMap";
        public const string OtherTMCache = "TMCache";

        /// <summary>是否启用虚拟摄像机实时渲染（仅普通渲染、且未启用其他渲染时）。</summary>
        public bool IsVirtualCamera
        {
            get { return RenderMode != null && !OtherRenderEnabled.Value && RenderMode.Value == RenderVirtualCamera; }
        }

        /// <summary>是否走实时渲染分支（VirtualCamera / TileMap / TMCache 都算）。</summary>
        public bool IsRealtimeRender
        {
            get
            {
                string mode = ActiveColorMode;
                return mode == RenderVirtualCamera || mode == OtherTileMap || mode == OtherTMCache;
            }
        }

        /// <summary>当前数据驱动着色模式。其他渲染启用时返回「其他渲染方式」，否则返回「渲染方式」。</summary>
        public string ActiveColorMode
        {
            get
            {
                if (OtherRenderEnabled.Value && OtherRenderMode != null)
                {
                    return OtherRenderMode.Value;
                }
                return RenderMode != null ? RenderMode.Value : RenderLegacy;
            }
        }

        // ---------- 传送 ----------
        public ConfigEntry<bool> TeleportEnabled;

        private void Awake()
        {
            Instance = this;
            BindConfig();

            _harmony = new Harmony(Info.Metadata.GUID);
            _harmony.PatchAll(Assembly.GetExecutingAssembly());

            // 路径点世界内光柱标记（第一人称视角可见的 3D 光柱 + 名称）
            GameObject markerGo = new GameObject("SDMap_WaypointMarkers");
            DontDestroyOnLoad(markerGo);
            markerGo.AddComponent<WaypointWorldMarker>();

            // 路径点存档组件（随游戏存档保存/读取，替代 JSON 文件）
            GameObject storageGo = new GameObject("SDMap_WaypointStorage");
            DontDestroyOnLoad(storageGo);
            storageGo.AddComponent<SDMapWaypointStorage>();

            Logger.LogInfo("SDMap loaded. Press " + ToggleMapKey.Value + " to open the world map.");
        }

        private void OnDestroy()
        {
            if (_harmony != null)
            {
                _harmony.UnpatchSelf();
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(ToggleMapKey.Value))
            {
                MapRenderer.SetWorldMapOpen(!MapRenderer.WorldMapOpen);
            }
            // ESC 关闭大地图
            if (MapRenderer.WorldMapOpen && Input.GetKeyDown(KeyCode.Escape))
            {
                MapRenderer.SetWorldMapOpen(false);
            }
        }

        private void OnGUI()
        {
            MapRenderer.Draw();
        }

        private void BindConfig()
        {
            ToggleMapKey = Config.Bind("快捷键", "打开大地图", KeyCode.M, "打开/关闭大地图的按键。");

            MinimapEnabled = Config.Bind("小地图", "显示小地图", true, "是否显示小地图。");
            MinimapSize = Config.Bind("小地图", "小地图尺寸", 160, new ConfigDescription("小地图边长（像素）。", new AcceptableValueRange<int>(80, 400)));
            MinimapZoom = Config.Bind("小地图", "小地图显示半径", 5f, "小地图显示的世界半径（世界单位，越小越精细）。");
            MinimapPosition = Config.Bind("小地图", "小地图位置", "右上", new ConfigDescription("小地图在屏幕上的位置。", new AcceptableValueList<string>("右上", "右下", "左上", "左下")));
            MinimapOpacity = Config.Bind("小地图", "小地图不透明度", 0.6f, new ConfigDescription("小地图背景不透明度（0~1）。", new AcceptableValueRange<float>(0.1f, 1f)));
            ShowCoordinates = Config.Bind("小地图", "显示坐标", true, "是否显示玩家坐标（X/Y/Z）。");
            MinimapRenderFPS = Config.Bind("小地图", "小地图采样帧率", 30, new ConfigDescription("小地图（虚拟摄像机渲染）每秒采样次数，越高越流畅但 CPU/GPU 开销越大。", new AcceptableValueRange<int>(1, 240)));

            MapSize = Config.Bind("大地图", "大地图尺寸", 700, new ConfigDescription("大地图边长（像素）。", new AcceptableValueRange<int>(240, 1400)));
            MapZoom = Config.Bind("大地图", "大地图默认缩放", 60f, "大地图默认缩放（越大越近越精细，1 = 显示整个世界）。");
            WorldmapRenderFPS = Config.Bind("大地图", "大地图采样帧率", 30, new ConfigDescription("大地图（虚拟摄像机渲染）每秒采样次数，越高越流畅但 CPU/GPU 开销越大。", new AcceptableValueRange<int>(1, 240)));

            RenderMode = Config.Bind(
                "渲染",
                "渲染方式",
                RenderVirtualCamera,
                new ConfigDescription(
                    "地图渲染方式（单选，点击循环切换）。\nLegacy：默认的高度图彩色分层纹理渲染，性能好、无额外开销。\nVirtualCamera：虚拟正交/透视摄像机实时渲染，相机随缩放/移动变高度与位置、渲染精度随缩放动态调整。\nShader：GPU 着色渲染（高程分层 + 法线光照，立体浮雕）。",
                    new AcceptableValueList<string>(RenderLegacy, RenderVirtualCamera, RenderShader)));
            LoadObjects = Config.Bind("渲染", "加载物体(Beta)", false, "允许虚拟摄像机强制加载所有岛屿物体（树/岩石/可交互物等），使其能被地图渲染。原版只有玩家附近的岛屿才加载物体。仅在 VirtualCamera 渲染下生效，切回其他渲染自动关闭。");
            UseOrthographic = Config.Bind("渲染", "正交相机", false, "勾选后，虚拟摄像机改为正交投影（高度恒定，全图均匀精度）。不勾选默认走透视投影（画面中心高 LOD、远端压缩，接近玩家视角）。该选项影响所有 VC-based 渲染（VirtualCamera / TileMap / TMCache）。");
            ShowPlayerMarkers = Config.Bind("渲染", "显示所有玩家(兼容MP)", true, "把小地图/大地图上所有玩家的位置标记出来（本地玩家白色箭头，其他玩家彩色圆点 + ID）。兼容联机多人模式。");

            OtherRenderEnabled = Config.Bind(
                "渲染",
                "启用其他渲染",
                false,
                "开启后改用「其他渲染方式」渲染地图，并锁定上方「渲染方式」（其值被忽略）。关闭后恢复普通渲染。");
            OtherRenderMode = Config.Bind(
                "渲染",
                "其他渲染方式",
                OtherNlight,
                new ConfigDescription(
                    "其他（实验性）渲染方式（单选，仅「启用其他渲染」开启时生效）。\n" +
                    "SoilMap：真实地表土壤分布渲染（沙/草/岩，游戏 soilMap 数据）。\n" +
                    "Nlight：高度图 + 法线光照，地形呈现山脊阴影立体感。\n" +
                    "Steepness：坡度着色，陡坡/缓坡分色显示。\n" +
                    "NLLayer：分层设色 + 法线光照（彩色 + 立体）。\n" +
                    "TileMap：单相机超高分辨率渲染（1400 RT，突破 1024 上限）。\n" +
                    "TMCache：超高分辨率渲染 + 快照缓存（中心/缩放变化小时复用）。",
                    new AcceptableValueList<string>(OtherSoilMap, OtherNlight, OtherSteepness, OtherNLLayer, OtherTileMap, OtherTMCache)));

            // 切回非 VC 渲染时，自动关闭「加载物体」
            RenderMode.SettingChanged += delegate (object s, System.EventArgs e)
            {
                if (RenderMode.Value != RenderVirtualCamera)
                {
                    LoadObjects.Value = false;
                }
            };

            TeleportEnabled = Config.Bind("传送", "允许传送", false, "开启后，可点击标记点传送到该位置。");
        }
    }
}
