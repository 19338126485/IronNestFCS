using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public enum Progress {
    Pending,
    Calculating,
    SelectingBullet,
    LoadingBullet,
    LoadingPowder,
    WaitLoading,
    Aiming,
    WaitingForFire,
    AwaitingStrikeTime,
    BackToIdle,
    Finished,
    Failed,
}

public class ArtilleryTask {
    public int targetId;
    public float angel;
    public float distance;
    public Vector3 position;
    public bool timed;              // 定时开火：装填调炮完成后待机，到 strikeTime − 飞行时间 自动击发
    public float strikeTime;        // 预定命中时刻（战场时钟秒，与报文 T= 同一时钟）
    public string strikeLabel = ""; // 日志用描述（打击点/命中时刻）
    public BulletType bulletType;
    public Progress progress;
}