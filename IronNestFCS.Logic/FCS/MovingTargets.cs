using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 一条移动目标的行进路线（列车或舰船），位置完全由时间函数确定：
///  - 列车：总站 + 轨道方位角 + 时刻表（路径点距离/时刻）拟合出匀速直线运动 d(t)，
///    p(t) = 总站 + dir(方位角) × d(t)。经过点事件行（"机车经过 路径点-A：P6 0:4"）
///    提供实测位置+时刻，作为最新参考点精化。
///  - 舰船：目击报告（时刻+经过点+航向+航速节）直接确定 p(t) = p0 + dir(航向) × v × (t-t0)。
///
/// 游戏时钟：报文 T= 时刻是 [TIMER &lt;MissionTime&gt;] 打印的 MissionStatsTracker.missionTime
/// （秒）的 HH:MM:SS 格式，故"现在"= MissionStatsTracker.Instance.mission.missionTime。
/// </summary>
public sealed class MovingTrack {
    public string Key = "";          // 登记簿 key（"列车→穆拉谷地总站" / 船名）
    public string DisplayName = "";
    public bool IsTrain;

    // 列车
    public Vector2 StationPos;
    public float BearingDeg;
    public float RefT;               // 参考时刻（最新已知点）
    public float RefDist;            // 参考时刻距总站的 km
    public float SpeedKmPs;          // 接近速度（d 每秒减小量）
    public float ArrivalT = -1f;
    public float StopT = -1f;        // ≥0：停车时刻（出轨/到站/轨道被击中）
    public float StopDistKm = -1f;

    // 舰船
    public float SightT;
    public Vector2 SightPos;
    public float HeadingDeg;
    public float SpeedKnots;

    private static Vector2 DirOf(float deg) =>
        new(Mathf.Sin(deg * Mathf.Deg2Rad), Mathf.Cos(deg * Mathf.Deg2Rad));

    /// <summary>t 时刻的预测网格位置（km）。列车到站/停车后静止；舰船匀速直线。</summary>
    public Vector2 PositionAt(float t) {
        if (!IsTrain) {
            var v = SpeedKnots * 1.852f / 3600f; // 节 → km/s
            return SightPos + DirOf(HeadingDeg) * (v * (t - SightT));
        }
        var d = RefDist - SpeedKmPs * (t - RefT);
        if (StopT >= 0f && t >= StopT) {
            d = StopDistKm >= 0f ? StopDistKm : RefDist - SpeedKmPs * (StopT - RefT);
        }
        if (d < 0f) d = 0f; // 到站后停在总站
        return StationPos + DirOf(BearingDeg) * d;
    }

    /// <summary>可交战窗口的右端（秒）：列车=到站时刻（已停车则无限），舰船=驶出地图时刻。</summary>
    public float LatestEngageTime(float now) {
        if (IsTrain) {
            if (StopT >= 0f && now >= StopT) return now + 3600f; // 已停车：静态目标
            if (ArrivalT > 0f) return ArrivalT;
            if (SpeedKmPs > 1e-6f) return RefT + RefDist / SpeedKmPs;
            return now + 1800f;
        }
        for (var t = now; t < now + 7200f; t += 10f) {
            if (!IntelSystem.IsOnMap(PositionAt(t))) return t;
        }
        return now + 7200f;
    }

    public string Describe() => IsTrain
        ? $"列车 站({StationPos.x:F2},{StationPos.y:F2}) 方位{BearingDeg:F0}° v={SpeedKmPs * 3600f:F1}km/h"
        : $"舰船 {DisplayName} 航向{HeadingDeg:F0}° {SpeedKnots:F1}节";
}

/// <summary>
/// 移动目标子系统：
///  - <see cref="Ingest"/>：Survey 后接收解析出的列车时刻表/舰船目击，构建/精化行进路线，
///    并把移动目标登记为候选（» 前缀、青色标记，位置随时间自动推进）。
///  - <see cref="FireMovingTarget"/>：对移动目标的"开火"——自动选定预定打击点
///    （最早满足 <see cref="MinPrepSeconds"/> 装填预算、且在可交战窗口内的时刻），
///    生成带 strikeTime 的定时任务入队；任务流程装填调炮完成后待机，
///    到"命中时刻 − 飞行时间"自动击发（飞行时间读 GunController.PredictedImpactTime + fireDelay）。
/// </summary>
public class MovingTargetSystem {
    /// <summary>装填准备时间预算（秒）：用户要求最好有 2 分钟以上装填时间。</summary>
    public const float MinPrepSeconds = 150f;

    private FSC? fcs;
    private IntelSystem? intel;
    private readonly Dictionary<string, MovingTrack> tracks = new();
    /// <summary>已做过时钟校验日志的事件（passage/目击的时刻），避免每次 Survey 重复打印。</summary>
    private readonly HashSet<string> clockChecked = new();

    public void Bind(FSC fcs, IntelSystem intel) {
        this.fcs = fcs;
        this.intel = intel;
    }

    public void ShutDown() {
        tracks.Clear();
        clockChecked.Clear();
        fcs = null;
        intel = null;
    }

    /// <summary>换任务时清空全部路线（IntelSystem.ResetMissionState 调用）。</summary>
    public void ResetMission() {
        tracks.Clear();
        clockChecked.Clear();
    }

    /// <summary>当前战场时间（秒，与报文 T= 时刻同一时钟）；&lt;0 = 时钟不可用。</summary>
    public float ScenarioNow() {
        try {
            var inst = MissionStatsTracker.Instance;
            if (inst == null || inst.mission == null) return -1f;
            return inst.mission.missionTime;
        }
        catch (Exception ex) {
            MelonLogger.Error($"[Moving] 读取任务时钟失败: {ex.Message}");
            return -1f;
        }
    }

    public static string FormatT(float seconds) {
        var s = Mathf.Max(0, Mathf.RoundToInt(seconds));
        return $"{s / 3600:D2}:{s / 60 % 60:D2}:{s % 60:D2}";
    }

    // ================= 情报摄入（每次 Survey 调用） =================

    public void Ingest(IntelDocument doc, bool verbose) {
        if (intel == null) return;
        var now = ScenarioNow();

        // 损毁除名（"已确认摧毁 <名称>"）
        foreach (var name in doc.DestroyedNames) {
            string? hit = null;
            foreach (var kv in tracks) {
                if (kv.Key.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                    name.Contains(kv.Value.DisplayName, StringComparison.OrdinalIgnoreCase)) {
                    hit = kv.Key;
                    break;
                }
            }
            if (hit != null) {
                tracks.Remove(hit);
                intel.RemoveMovingCandidate(hit, "已确认摧毁");
            }
        }

        if (doc.Train != null) IngestTrain(doc.Train, now, verbose);
        foreach (var s in doc.Ships) IngestShip(s, now, verbose);
    }

    private void IngestTrain(TrainScheduleRaw raw, float now, bool verbose) {
        const string key = "列车";
        if (raw.Destroyed) {
            if (tracks.Remove(key)) intel!.RemoveMovingCandidate(key, "列车已摧毁");
            return;
        }

        // 车站位置：时刻表自带 gridref（"…总站：J6 0:4"）；缺了才回退锚点字典
        Vector2 station;
        if (!float.IsNaN(raw.StationGx)) {
            station = new Vector2(raw.StationGx, raw.StationGy);
        }
        else if (intel!.TryGetAnchorOptions(raw.StationName.Length > 0 ? raw.StationName : "总站", out var opts)
                 && opts.Count > 0) {
            station = opts[0];
        }
        else {
            if (verbose) MelonLogger.Msg("[Moving] 列车时刻表缺车站坐标，暂不建模");
            return;
        }
        if (raw.BearingDeg < 0f) {
            if (verbose) MelonLogger.Msg("[Moving] 列车时刻表缺轨道方位角，暂不建模");
            return;
        }

        // (时刻, 距车站km) 样本点：时刻表路径点 + 经过点事件（实测位置换算回沿线距离）
        var dir = new Vector2(Mathf.Sin(raw.BearingDeg * Mathf.Deg2Rad), Mathf.Cos(raw.BearingDeg * Mathf.Deg2Rad));
        var samples = new List<(float t, float d)>();
        foreach (var w in raw.Waypoints) samples.Add((w.t, w.distKm));
        foreach (var p in raw.Passages) {
            var rel = new Vector2(p.gx, p.gy) - station;
            var d = rel.x * dir.x + rel.y * dir.y;
            if (d > 0.05f) samples.Add((p.t, d)); // 在轨道射线前方才有效
            ClockCheck($"passage:{p.name}:{p.t}", p.t, now);
        }
        if (raw.ArrivalT > 0f) samples.Add((raw.ArrivalT, 0f)); // 到站时刻 = 距站 0
        if (samples.Count < 2) {
            if (verbose) MelonLogger.Msg("[Moving] 列车时刻表样本不足（<2 个时刻点），暂不建模");
            return;
        }
        samples.Sort((a, b) => a.t.CompareTo(b.t));

        // 匀速拟合：首末样本点求速度（报文时刻精确到秒，首尾相距最远误差最小）
        var first = samples[0];
        var last = samples[^1];
        float speed;
        if (last.t - first.t > 1f) {
            speed = (first.d - last.d) / (last.t - first.t);
        }
        else {
            speed = tracks.TryGetValue(key, out var old) ? old.SpeedKmPs : 0f;
        }
        if (speed <= 1e-5f) {
            if (verbose) MelonLogger.Msg("[Moving] 列车速度拟合失败（样本时刻重合）");
            return;
        }

        // 参考点取时间上最新的样本（误差随外推距离增长，用最新点向未来外推最短）
        var refIdx = samples.Count - 1;
        // 但若最新样本是到站时刻（d=0）而列车还在途中，用途中最新样本
        for (var i = samples.Count - 1; i >= 0; --i) {
            if (samples[i].t <= now || i == 0) { refIdx = i; break; }
        }
        var refSample = samples[refIdx];

        var stopT = raw.StopT == -3f ? now : raw.StopT;
        var track = new MovingTrack {
            Key = key,
            DisplayName = raw.StationName.Length > 0 ? $"列车→{raw.StationName}" : "列车",
            IsTrain = true,
            StationPos = station,
            BearingDeg = raw.BearingDeg,
            RefT = refSample.t,
            RefDist = refSample.d,
            SpeedKmPs = speed,
            ArrivalT = raw.ArrivalT,
            StopT = stopT,
            StopDistKm = raw.StopDistKm,
        };
        tracks[key] = track;

        var pos = track.PositionAt(now);
        intel!.UpsertMovingCandidate(key, track.DisplayName, pos, "MapToken_Artillery", track);
        if (verbose) {
            MelonLogger.Msg($"[Moving] {track.Describe()} 样本{samples.Count}个 参考点t={FormatT(refSample.t)} " +
                            $"d={refSample.d:F2}km 当前位置 ({pos.x:F2},{pos.y:F2})" +
                            (raw.ArrivalT > 0f ? $" 到站T={FormatT(raw.ArrivalT)}" : ""));
        }
    }

    private void IngestShip(ShipSighting s, float now, bool verbose) {
        if (s.T < 0f || s.Knots <= 0f || s.HeadingDeg < 0f) {
            if (verbose) MelonLogger.Msg($"[Moving] 舰船报告[{s.Name}]信息不全（时刻/航向/航速缺），跳过");
            return;
        }
        var key = s.Name;
        if (tracks.TryGetValue(key, out var old) && old.SightT >= s.T - 0.5f) return; // 旧报告（纸带重印）

        ClockCheck($"ship:{s.Name}:{s.T}", s.T, now);
        var track = new MovingTrack {
            Key = key,
            DisplayName = s.Name,
            IsTrain = false,
            SightT = s.T,
            SightPos = new Vector2(s.Gx, s.Gy),
            HeadingDeg = s.HeadingDeg,
            SpeedKnots = s.Knots,
        };
        tracks[key] = track;
        var pos = track.PositionAt(now);
        intel!.UpsertMovingCandidate(key, s.Name, pos, "MapToken_Artillery", track);
        MelonLogger.Msg($"[Moving] {track.Describe()} 目击T={FormatT(s.T)} @ ({s.Gx:F2},{s.Gy:F2}) " +
                        $"当前位置 ({pos.x:F2},{pos.y:F2})");
    }

    /// <summary>
    /// 时钟校验：事件行（时刻 T）首次出现时记录 当前missionTime − T。
    /// 预期为小的正值（打印队列延迟，几秒量级）；若系统性巨大偏移说明
    /// MissionTime 与报文 T= 不是同一时钟，需要回头校准。
    /// </summary>
    private void ClockCheck(string eventKey, float eventT, float now) {
        if (now < 0f || !clockChecked.Add(eventKey)) return;
        MelonLogger.Msg($"[Moving] 时钟校验: 事件T={FormatT(eventT)} 首次见于 missionTime={FormatT(now)} " +
                        $"(打印延迟 {now - eventT:F1}s)");
    }

    // ================= 定时开火 =================

    /// <summary>
    /// 对移动目标开火：自动选定预定打击点并生成定时任务。
    /// 打击时刻 = max(现在 + 装填预算, 进入地图时刻)，钳制在可交战窗口内；
    /// 任务装填调炮完成后待机，到 命中时刻 − 飞行时间 自动击发。
    /// </summary>
    public void FireMovingTarget(MovingTrack track) {
        if (fcs == null || intel == null) {
            MelonLogger.Error("[Moving] 未绑定，无法开火");
            return;
        }
        var now = ScenarioNow();
        if (now < 0f) {
            MelonLogger.Error("[Moving] 任务时钟不可用（不在战斗场景？），无法定时开火");
            return;
        }

        var tEnd = track.LatestEngageTime(now) - 5f; // 留 5s 余量，别卡着到站/出图瞬间
        var tEnter = FirstOnMapTime(track, now, tEnd);
        var ts = Mathf.Max(now + MinPrepSeconds, tEnter + 5f);
        var rushed = false;
        if (ts > tEnd) {
            ts = tEnd;
            rushed = true;
        }
        if (ts < now + 15f) {
            MelonLogger.Warning($"[Moving] [{track.DisplayName}] 来不及装填（窗口仅剩 {tEnd - now:F0}s），放弃定时开火");
            return;
        }

        var strike = track.PositionAt(ts);
        if (!IntelSystem.IsOnMap(strike)) {
            MelonLogger.Warning($"[Moving] [{track.DisplayName}] 打击点 ({strike.x:F2},{strike.y:F2}) 在图外，放弃");
            return;
        }
        if (!intel.TryGetFiringSolution(strike, out var angle, out var dist)) {
            MelonLogger.Error("[Moving] 仿射校准/炮塔不可用，无法生成射击诸元（先按 Survey）");
            return;
        }

        var task = new ArtilleryTask {
            targetId = 0,
            angel = angle,
            distance = dist,
            position = new Vector3(strike.x, strike.y, 0f),
            bulletType = fcs.Interactor.selectedBulletType,
            timed = true,
            strikeTime = ts,
            strikeLabel = $"[{track.DisplayName}] 打击点({strike.x:F2},{strike.y:F2}) 命中T={FormatT(ts)}",
        };
        MelonLogger.Msg($"[Moving] 定时开火 {task.strikeLabel} {angle:F1}°/{dist:F2}km {task.bulletType} " +
                        $"装填预算 {(ts - now):F0}s{(rushed ? "（窗口不足，仓促射击）" : "")}");
        fcs.EnqueueTask(task);
    }

    /// <summary>目标预测位置进入地图边界的最早时刻（采样步进 5s）。已在图上返回 now。</summary>
    private static float FirstOnMapTime(MovingTrack track, float now, float tEnd) {
        for (var t = now; t <= tEnd; t += 5f) {
            if (IntelSystem.IsOnMap(track.PositionAt(t))) return t;
        }
        return tEnd;
    }
}
