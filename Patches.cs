using HarmonyLib;

namespace SDMap
{
    /// <summary>
    /// 当「加载物体」开启时，阻止游戏卸载岛屿（Zone）。
    /// 原版 PollUnload 会在玩家不在附近时 SaveZone 卸载岛屿，与 LoadAllZonesForRender 的强制加载冲突，
    /// 导致「加载→卸载」反复横跳、破坏 Zone 状态机，产生大量异常（画面卡灰的根因）。
    /// 开启「加载物体」时直接返回 false（不卸载），让强制加载的物体保持激活、能被虚拟摄像机渲染。
    /// </summary>
    [HarmonyPatch(typeof(StrandedWorld), "PollUnload")]
    internal static class Patch_PollUnload
    {
        private static bool Prefix(ref bool __result)
        {
            if (Main.Instance != null && Main.Instance.LoadObjects.Value)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }
}
