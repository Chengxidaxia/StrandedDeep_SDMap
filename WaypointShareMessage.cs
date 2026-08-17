using Funlabs;

namespace SDMap
{
    /// <summary>
    /// 路径点分享消息：一次打包一个路径点的完整信息（名称 + 坐标 X/Y/Z + 颜色），
    /// 接收方按坐标 upsert（同坐标更新、否则新增）。不做增删改的持续同步——只在玩家主动点「分享」时发送。
    /// 继承游戏自带的 MultiplayerMessage，借助其 [Replicate] 序列化与 MessageGlobalEvent 广播机制。
    /// 注意：所有参与联机的客户端都必须加载了 SDMap（否则消息类型集合不一致，id 错位）。
    /// OnPeer 只在"非发送方"的 peer 上触发（MultiplayerMng.OnEvent 里 FromSelf 直接 return），不会回环。
    /// </summary>
    public class WaypointShareMessage : MultiplayerMessage
    {
        [Replicate]
        public string Name;

        [Replicate]
        public float X;

        [Replicate]
        public float Z;

        [Replicate]
        public float Y;

        [Replicate]
        public int ColorIndex;

        public override void OnPeer()
        {
            WaypointManager.ApplyShared(Name, X, Z, Y, ColorIndex);
        }
    }
}
