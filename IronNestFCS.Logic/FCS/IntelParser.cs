using System.Text.RegularExpressions;
using MelonLoader;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 从一行情报文本里抠出的结构化条目。
/// Kind: "gridref"（网格坐标，直接定位）/ "angleDist"（标尺誊抄，锚点=炮位）
///       / "bearing"（方位线）/ "distance"（距离圆）。
/// gridref: Value1=x, Value2=y（km）；bearing: Value1=方位角；distance: Value1=距离(km)；
/// angleDist: Value1=角度, Value2=距离。
/// </summary>
public sealed class ParsedItem {
    public string RawLine = "";
    public string Kind = "";
    public string AnchorText = ""; // 锚点名（如 观测员#1），空 = 炮位；turretZone 时存大格编号（如 H4）
    public float Value1;
    public float Value2;
    public float Value3;           // turretDist/turretBearing/foDist/foBearing：Value1=距离/角度, Value2=报点x, Value3=报点y
    public bool Fuzzy;             // 模糊约束（罗盘方位词，±11°）：解算照旧但候选标低置信度
    public string? TokenName;      // 行内识别出的棋子类型名
}

/// <summary>一个情报主题（如"敌方炮兵指挥中心#1:"），其下的"自…"约束都归到它名下。</summary>
public sealed class IntelSubject {
    public string Name = "";
    public string? TokenName;
    public readonly List<ParsedItem> Constraints = new();
}

/// <summary>
/// 列车时刻表原始数据（移动目标，模板见 resources.assets 本地化串"列车时刻表"）：
/// 到达站"穆拉谷地 总站：J6 0:4"、"估计到站时间：T=10:16:50"、
/// "铁路线走向笔直。自总站的方位角 090°。"、"路径点-A - 距车站6.00km：T=10:06:50"。
/// 运行事件："- 10:06:50 - 机车经过 路径点-A：P6 0:4。"、"列车停止中 - 10:06:51"+"距总站 6.00km。"。
/// 报文里的 T= 时刻就是 [TIMER &lt;MissionTime&gt;] 打印的 MissionStatsTracker.missionTime（秒）。
/// StopT 哨兵值：-1=未停止，-3="已出轨/已抵达"（停止时刻未知，取 ingest 时的当前时刻）。
/// </summary>
public sealed class TrainScheduleRaw {
    public string StationName = "";
    public float StationGx = float.NaN, StationGy = float.NaN;
    public float BearingDeg = -1f;
    public float ArrivalT = -1f;
    public readonly List<(string name, float distKm, float t)> Waypoints = new();
    public readonly List<(string name, float t, float gx, float gy)> Passages = new();
    public float StopT = -1f;
    public float StopDistKm = -1f;
    public bool Destroyed;
}

/// <summary>
/// 舰船目击报告（模板"舰船数据"）："<船名>已发现：" + "在 T:10:06:50 时经过 P6 0:4。"
/// + "以9.7节速度航行12°航向。"。航速单位节（1节=1.852km/h，模板自证：9.7节=20秒0.10千米）。
/// </summary>
public sealed class ShipSighting {
    public string Name = "";
    public float T = -1f;
    public float Gx, Gy;
    public float HeadingDeg = -1f;
    public float Knots = -1f;
}

/// <summary>一段情报文本的解析结果：散条目（gridref 等）+ 有序主题列表 + 移动目标数据。</summary>
public sealed class IntelDocument {
    public readonly List<ParsedItem> Items = new();
    public readonly List<IntelSubject> Subjects = new();
    public TrainScheduleRaw? Train;
    public readonly List<ShipSighting> Ships = new();
    /// <summary>报文里确认摧毁的移动目标名（"已确认摧毁 <名称>"）。</summary>
    public readonly List<string> DestroyedNames = new();

    public void Merge(IntelDocument other) {
        Items.AddRange(other.Items);
        Subjects.AddRange(other.Subjects);
        if (other.Train != null) {
            Train ??= new TrainScheduleRaw();
            var t = Train;
            var o = other.Train;
            if (t.StationName.Length == 0) {
                t.StationName = o.StationName;
                t.StationGx = o.StationGx;
                t.StationGy = o.StationGy;
            }
            if (t.BearingDeg < 0f) t.BearingDeg = o.BearingDeg;
            if (t.ArrivalT < 0f) t.ArrivalT = o.ArrivalT;
            foreach (var w in o.Waypoints) {
                if (!t.Waypoints.Exists(x => x.name == w.name && Math.Abs(x.t - w.t) < 0.5f)) t.Waypoints.Add(w);
            }
            foreach (var p in o.Passages) {
                if (!t.Passages.Exists(x => x.name == p.name && Math.Abs(x.t - p.t) < 0.5f)) t.Passages.Add(p);
            }
            if (o.StopT == -3f) t.StopT = -3f;
            else if (o.StopT > t.StopT) t.StopT = o.StopT;
            if (o.StopDistKm >= 0f) t.StopDistKm = o.StopDistKm;
            t.Destroyed |= o.Destroyed;
        }
        Ships.AddRange(other.Ships);
        DestroyedNames.AddRange(other.DestroyedNames);
    }
}

/// <summary>
/// 情报文本解析器（笔记本 / 两台电报机纸带）。
///
/// 格式均来自 2026-08-12 实测报文（游戏语言简中）：
///  - 参考点行："铁巢 - P2 2:6"、"观测员#1 - M3 6:1"、"卡斯特尔德费尔斯海滩 总站：J8 0:0"
///  - 主题行：  "敌方炮兵指挥中心#1:"、"参考点Alpha:"（后续约束行归其名下）
///  - 约束行：  "自观测员#1的方位角293°" / "自观测员#2的距离11.32km"
///              / "自敌方炮兵指挥中心#2的方位角271°及距离3.74km"（一行两约束，链式锚点）
/// 纸带全文带 TMP 富文本标记（&lt;b&gt; 等），解析前先剥掉。
/// 含数字却匹配不上的行原样进日志，作为新格式的样本。
/// </summary>
public static class IntelParser {
    private static readonly Regex RichTag = new(@"<[^>]+>", RegexOptions.Compiled);

    // 网格坐标：名称 -/： 列字母+行号 子列:子行。列 A=0，行从 1 起，子格为格内第一位小数。
    // 取子格【中心】（+0.05km）：报文格只有 0.1km 精度，实体位于所指子格中央。
    // 实测三组独立对照全部精确 +0.05："M3 6:1"→实体(12.65,2.15)；FO报点"G1 9:4"→FO实体(6.95,0.45)；
    // "A1 0:2"→FO实体(0.05,0.25)。取角点则系统性偏 50~70m（2026-08-13 用户观测确认）。
    private const float SubGridCenter = 0.05f;
    private static readonly Regex GridRef =
        new(@"^(?<name>.+?)\s*[-–—:：]\s*(?<col>[A-Za-z])(?<row>\d{1,2})\s+(?<sx>\d)\s*[:：]\s*(?<sy>\d)\s*$",
            RegexOptions.Compiled);

    // 主题行：整行以冒号收尾（且不是"自…"约束行）。
    private static readonly Regex SubjectHeader =
        new(@"^(?<name>\S.*?)\s*[:：]\s*$", RegexOptions.Compiled);

    // 约束："自<锚点>的方位角<度>°" / "自<锚点>的距离<值><单位>" / 同行追加"及距离<值><单位>"。
    private static readonly Regex BearingPart =
        new(@"自\s*(?<anchor>.+?)\s*的方位角\s*(?<deg>\d{1,3})\s*°", RegexOptions.Compiled);
    private static readonly Regex DistancePart =
        new(@"自\s*(?<anchor>.+?)\s*的距离\s*(?<dist>[\d.]+)\s*(?<unit>km|公里|千米|米)\b", RegexOptions.Compiled);
    private static readonly Regex DistanceTail =
        new(@"及距离\s*(?<dist>[\d.]+)\s*(?<unit>km|公里|千米|米)\b", RegexOptions.Compiled);

    // MarkerNoteLogger 默认格式（玩家在地图上拖标尺后自动誊抄）：'Angle: {angle} | Distance: {distance}'
    private static readonly Regex AngleDistance =
        new(@"Angle:\s*([\d.]+)\s*\|\s*Distance:\s*([\d.]+)", RegexOptions.Compiled);

    // ===== 紧急转移 / 后勤车队（位置报告卡）报文，2026-08-12 实测样本 =====
    // "铁巢新位置位于H4某处" —— 转移完成后告知大格（后勤车队卡的参数）
    private static readonly Regex TurretZone =
        new(@"位于\s*(?<cell>[A-Z]\d{1,2})\s*某处", RegexOptions.Compiled);
    // "测距仪显示我与铁巢相距0.61km：车队#2当前位置：H4 8:4 . . ." —— 距离圆约束（圆心=车队位置）
    private static readonly Regex ConvoyDistance =
        new(@"相距\s*(?<dist>[\d.]+)\s*(?:km|公里|千米).*?(?<col>[A-Z])(?<row>\d{1,2})\s+(?<sx>\d)\s*[:：]\s*(?<sy>\d)",
            RegexOptions.Compiled);
    // "FO[#n] 音频报告 敌方观测员#10: 1.92km 自 F5 3:0 . . ."
    // 前线观测员（Spotter 卡）报告离自己最近的敌军：敌军的距离圆约束（圆心=FO 所在报点）。
    private static readonly Regex FoReport =
        new(@"^FO.*报告\s*(?<name>.+?)\s*[:：]\s*(?<dist>[\d.]+)\s*(?<unit>km|公里|千米|米)\b\s*自\s*(?<col>[A-Z])(?<row>\d{1,2})\s+(?<sx>\d)\s*[:：]\s*(?<sy>\d)",
            RegexOptions.Compiled);

    // "FO[#n]发现 敌方观测员#9: 313 自 G1 9:4" / "FO发现 幽灵炮台: 049 自 A1 0:2"
    // 前线观测员的方位线报告：<度> 自 <FO 报点>，罗盘约定（实测：313° 自 (6.9,0.4) → 敌军真位 (3.86,3.33)）。
    // 必须先于 ConvoyBearing 匹配——结构同为"<度> 自 <格>"，不拦截会被误吞成铁巢约束。
    private static readonly Regex FoBearing =
        new(@"^FO.*?发现\s*(?<name>.+?)\s*[:：]\s*(?<deg>\d{1,3})\s*自\s*(?<col>[A-Z])(?<row>\d{1,2})\s+(?<sx>\d)\s*[:：]\s*(?<sy>\d)",
            RegexOptions.Compiled);

    // ===== 活动报告（观测员三角测量，2026-08-13 实测样本） =====
    // 一组格式：汇总行"报告活动于坐标 B10" + 每个观测员一行测量：
    //   "观测员#2：8.91km"（距离）/ "观测员#3：254°"（方位角）/ "观测员#1：西北偏西"（罗盘方位词，±11°模糊）
    // 实测纸带（倒序）：汇总行在测量行【之上】（汇总后打印），归属给解析顺序中紧随其后的测量块。
    private static readonly Regex ActivityHeader =
        new(@"报告活动于坐标\s*(?<cell>[A-Z]\d{1,2})", RegexOptions.Compiled);
    private static readonly Regex ActivityDist =
        new(@"^(?<anchor>[^\s：:]+)[:：]\s*(?<dist>[\d.]+)\s*(?<unit>km|公里|千米)\s*$", RegexOptions.Compiled);
    private static readonly Regex ActivityBearing =
        new(@"^(?<anchor>[^\s：:]+)[:：]\s*(?<deg>\d{1,3})\s*°\s*$", RegexOptions.Compiled);
    private static readonly Regex ActivityCompass =
        new(@"^(?<anchor>[^\s：:]+)[:：]\s*(?<dir>[北东南西偏]+)\s*$", RegexOptions.Compiled);

    /// <summary>中文十六向罗盘词 → 方位角（罗盘约定：0=北，顺时针）。两种偏词写法全收（西偏北/西北偏西、西南偏西等）。</summary>
    private static readonly (string word, float deg)[] CompassWords = {
        ("北", 0f), ("北偏东", 22.5f), ("东北偏北", 22.5f),
        ("东北", 45f), ("东偏北", 67.5f), ("东北偏东", 67.5f),
        ("东", 90f), ("东偏南", 112.5f), ("东南偏东", 112.5f),
        ("东南", 135f), ("南偏东", 157.5f), ("东南偏南", 157.5f),
        ("南", 180f), ("南偏西", 202.5f), ("西南偏南", 202.5f),
        ("西南", 225f), ("西偏南", 247.5f), ("西南偏西", 247.5f),
        ("西", 270f), ("西偏北", 292.5f), ("西北偏西", 292.5f),
        ("西北", 315f), ("北偏西", 337.5f), ("西北偏北", 337.5f),
    };

    // "车队#3发现铁巢: <风味文本> 180 自 H4 2:7 . . ." —— 方位线约束（起点=报点，结构为"<度> 自 <格>"）
    private static readonly Regex ConvoyBearing =
        new(@"(?<deg>\d{1,3})\s*自\s*(?<col>[A-Z])(?<row>\d{1,2})\s+(?<sx>\d)\s*[:：]\s*(?<sy>\d)",
            RegexOptions.Compiled);

    // ===== 列车时刻表 / 舰船动态（移动目标，模板见 resources.assets，2026-08-13 实测） =====
    // "铁路线走向笔直。自总站的方位角 090°。"（方位角锚点即总站，无需再解析锚点名）
    private static readonly Regex TrainBearing =
        new(@"铁路线走向笔直.*?自\s*(?<anchor>.+?)\s*的方位角\s*(?<deg>\d{1,3})", RegexOptions.Compiled);
    // "路径点-A - 距车站6.00km：T=10:06:50"
    private static readonly Regex TrainWaypoint =
        new(@"^(?<name>\S+?)\s*[-–—]\s*距(?:离)?车站\s*(?<dist>[\d.]+)\s*(?:km|公里|千米)\s*[：:]\s*T=(?<h>\d{1,2}):(?<mi>\d{2}):(?<s>\d{2})",
            RegexOptions.Compiled);
    // "到达时间：T=10:33:30" / "估计到站时间：T=10:16:50"
    private static readonly Regex TrainArrival =
        new(@"(?:到达时间|估计到站时间|到站时间)\s*[：:]\s*T=(?<h>\d{1,2}):(?<mi>\d{2}):(?<s>\d{2})",
            RegexOptions.Compiled);
    // "- 10:06:50 - 机车经过 路径点-A：P6 0:4。"（实测位置+时刻，同时用于时钟校验）
    private static readonly Regex TrainPassage =
        new(@"^-\s*(?<h>\d{1,2}):(?<mi>\d{2}):(?<s>\d{2})\s*-\s*机车经过\s*(?<name>.+?)\s*[：:]\s*(?<col>[A-Z])(?<row>\d{1,2})\s+(?<sx>\d)\s*[:：]\s*(?<sy>\d)",
            RegexOptions.Compiled);
    // "列车停止中 - 10:06:51"
    private static readonly Regex TrainStop =
        new(@"列车停止中\s*-\s*(?<h>\d{1,2}):(?<mi>\d{2}):(?<s>\d{2})", RegexOptions.Compiled);
    // "距总站 6.00km。"（停止时距总站的距离，与停止时刻配对）
    private static readonly Regex TrainStopDist =
        new(@"^距\s*总站\s*(?<dist>[\d.]+)\s*(?:km|公里|千米)", RegexOptions.Compiled);
    // 舰船目击："<船名>已发现："（块头）+ "在 T:10:06:50 时经过 P6 0:4。" + "以9.7节速度航行12°航向。"
    private static readonly Regex ShipHeader =
        new(@"^(?<name>.+?)已发现\s*[：:]\s*$", RegexOptions.Compiled);
    private static readonly Regex ShipPosTime =
        new(@"在\s*T\s*[:：=]\s*(?<h>\d{1,2}):(?<mi>\d{2}):(?<s>\d{2})\s*时经[过過]\s*(?<col>[A-Z])(?<row>\d{1,2})\s+(?<sx>\d)\s*[:：]\s*(?<sy>\d)",
            RegexOptions.Compiled);
    private static readonly Regex ShipCourse1 =
        new(@"以\s*(?<kn>[\d.]+)\s*节速度航行\s*(?<deg>\d{1,3})\s*°?\s*航向", RegexOptions.Compiled);
    private static readonly Regex ShipCourse2 =
        new(@"以\s*(?<kn>[\d.]+)\s*节之航速沿\s*(?<deg>\d{1,3})\s*°\s*航向", RegexOptions.Compiled);
    private static readonly Regex ShipCourse3 =
        new(@"正以\s*(?<deg>\d{1,3})\s*°\s*航向[、,，]\s*航速\s*(?<kn>[\d.]+)\s*节", RegexOptions.Compiled);
    // "已确认摧毁 皇家海军罗金厄姆号。"（移动目标损毁除名）
    private static readonly Regex DestroyedReport =
        new(@"已确认摧毁\s*(?<name>[^。.\n]+)", RegexOptions.Compiled);

    /// <summary>"HH:MM:SS" → 秒（报文 T= 时刻即 MissionStatsTracker.missionTime 的格式化）。</summary>
    private static float Hms(Match m) =>
        ParseFloat(m.Groups["h"].Value) * 3600f + ParseFloat(m.Groups["mi"].Value) * 60f + ParseFloat(m.Groups["s"].Value);

    /// <summary>裸网格坐标（无名称前缀，如 "H4 2:7"）→ 网格公里坐标（子格中心），与 GridRef 行同一换算。</summary>
    private static (float gx, float gy) ParseBareGrid(Match m, string p) => (
        char.ToUpperInvariant(m.Groups[p + "col"].Value[0]) - 'A' + ParseFloat(m.Groups[p + "sx"].Value) / 10f + SubGridCenter,
        ParseFloat(m.Groups[p + "row"].Value) - 1f + ParseFloat(m.Groups[p + "sy"].Value) / 10f + SubGridCenter);

    // 单位类型关键词 → 棋子名。棋子类型实测只有三种：
    // MapToken_Artillery（数字 1-10，FCS 打击目标）、MapToken_RefrencePoint（字母 A-E）、MapToken_Recon（数字 1-10）。
    // 作战单位一律用炮兵目标棋（带编号、可直接被 FCS 解算打击）；参考点用参考点棋。
    private static readonly (string keyword, string token)[] UnitKeywords = {
        ("参考点", "MapToken_RefrencePoint"), ("路径点", "MapToken_RefrencePoint"),
        ("waypoint", "MapToken_RefrencePoint"), ("reference", "MapToken_RefrencePoint"),
        ("炮兵", "MapToken_Artillery"), ("artiller", "MapToken_Artillery"),
        ("炮台", "MapToken_Artillery"), ("battery", "MapToken_Artillery"),
        ("指挥", "MapToken_Artillery"), ("fdc", "MapToken_Artillery"),
        ("坦克", "MapToken_Artillery"), ("tank", "MapToken_Artillery"),
        ("步兵", "MapToken_Artillery"), ("infantry", "MapToken_Artillery"),
        ("工事", "MapToken_Artillery"), ("碉堡", "MapToken_Artillery"), ("fort", "MapToken_Artillery"),
        ("防空", "MapToken_Artillery"), ("aa", "MapToken_Artillery"),
        ("列车", "MapToken_Artillery"), ("train", "MapToken_Artillery"),
    };

    /// <summary>
    /// 解析一段情报文本。主题分组：主题行之后的"自…"约束行都挂到该主题，
    /// 直到下一个主题行；不属于任何主题的约束进散条目池。
    /// </summary>
    public static IntelDocument Parse(string text, string sourceTag) {
        var doc = new IntelDocument();
        if (string.IsNullOrWhiteSpace(text)) return doc;

        IntelSubject? current = null;
        ShipSighting? pendingShip = null; // "已发现："块头开启，随后两行填时刻/经过点与航向/航速
        foreach (var rawLine in text.Split('\n')) {
            var line = RichTag.Replace(rawLine, "").Trim();
            if (line.Length == 0 || line == "." || line == "- - -") continue;

            // ===== 移动目标：列车时刻表 / 舰船目击（必须先于主题行、T= 跳过等通用逻辑） =====
            if (line.Contains("轨道走向") || line.Contains("进入段") || line.Contains("列车时刻表")) continue;
            var mTrain = TrainBearing.Match(line);
            if (mTrain.Success) {
                EnsureTrain(doc).BearingDeg = ParseFloat(mTrain.Groups["deg"].Value);
                continue;
            }
            mTrain = TrainWaypoint.Match(line);
            if (mTrain.Success) {
                EnsureTrain(doc).Waypoints.Add((mTrain.Groups["name"].Value.Trim(),
                    ParseFloat(mTrain.Groups["dist"].Value), Hms(mTrain)));
                continue;
            }
            mTrain = TrainArrival.Match(line);
            if (mTrain.Success) {
                EnsureTrain(doc).ArrivalT = Hms(mTrain);
                continue;
            }
            mTrain = TrainPassage.Match(line);
            if (mTrain.Success) {
                var (pgx, pgy) = ParseBareGrid(mTrain, "");
                EnsureTrain(doc).Passages.Add((mTrain.Groups["name"].Value.Trim(), Hms(mTrain), pgx, pgy));
                continue;
            }
            mTrain = TrainStop.Match(line);
            if (mTrain.Success) {
                var tr = EnsureTrain(doc);
                var t = Hms(mTrain);
                if (t > tr.StopT) tr.StopT = t; // 取最新停止时刻
                continue;
            }
            mTrain = TrainStopDist.Match(line);
            if (mTrain.Success) {
                EnsureTrain(doc).StopDistKm = ParseFloat(mTrain.Groups["dist"].Value);
                continue;
            }
            if (line.Contains("列车已出轨")) { EnsureTrain(doc).StopT = -3f; continue; }
            if (line.Contains("列车已抵达") || line.Contains("列车已到达")) {
                var tr = EnsureTrain(doc);
                tr.StopT = -3f;      // 到站=停在总站（时刻未给出时取 ingest 当前时刻）
                tr.StopDistKm = 0f;
                continue;
            }
            if (line.Contains("列车被完全摧毁") || line.Contains("增援列车已摧毁")) {
                EnsureTrain(doc).Destroyed = true;
                continue;
            }
            var mShip = ShipHeader.Match(line);
            if (mShip.Success) {
                pendingShip = new ShipSighting { Name = mShip.Groups["name"].Value.Trim() };
                doc.Ships.Add(pendingShip);
                continue;
            }
            mShip = ShipPosTime.Match(line);
            if (mShip.Success) {
                if (pendingShip != null) {
                    var (sgx, sgy) = ParseBareGrid(mShip, "");
                    pendingShip.T = Hms(mShip);
                    pendingShip.Gx = sgx;
                    pendingShip.Gy = sgy;
                }
                continue;
            }
            mShip = ShipCourse1.Match(line);
            if (!mShip.Success) mShip = ShipCourse2.Match(line);
            if (!mShip.Success) mShip = ShipCourse3.Match(line);
            if (mShip.Success) {
                if (pendingShip != null) {
                    pendingShip.Knots = ParseFloat(mShip.Groups["kn"].Value);
                    pendingShip.HeadingDeg = ParseFloat(mShip.Groups["deg"].Value);
                }
                continue;
            }
            // "9.7节 = 20秒行驶0.10千米。"等换算说明行：无独立信息，静默消费（否则进未解析日志刷屏）
            if (line.Contains("节 =") || line.Contains("节=")) continue;
            var mDestroyed = DestroyedReport.Match(line);
            if (mDestroyed.Success) {
                doc.DestroyedNames.Add(mDestroyed.Groups["name"].Value.Trim());
                continue;
            }

            // ===== 活动报告（观测员三角测量） =====
            // 实测结构（截图确认）：与其他报文同一"主题+约束"格式——
            //   "敌方集结区："（标准主题行，SubjectHeader 解析）+ 每个观测员一行测量。
            // 测量行只需翻译成约束挂到当前主题名下，不需要任何特殊分组。
            // 汇总行"报告活动于坐标 M3"只是 1km 精度的大格提示，无几何价值，静默消费。
            var mAct = ActivityHeader.Match(line);
            if (mAct.Success) continue;

            // 测量行：距离 / 方位角 / 罗盘方位词（模糊）。有主题归主题，无主题进散条目池。
            var ma = ActivityDist.Match(line);
            if (ma.Success) {
                (current != null ? current.Constraints : doc.Items).Add(new ParsedItem {
                    RawLine = line, Kind = "distance", AnchorText = ma.Groups["anchor"].Value.Trim(),
                    Value1 = ToKm(ma),
                });
                continue;
            }
            ma = ActivityBearing.Match(line);
            if (ma.Success) {
                (current != null ? current.Constraints : doc.Items).Add(new ParsedItem {
                    RawLine = line, Kind = "bearing", AnchorText = ma.Groups["anchor"].Value.Trim(),
                    Value1 = ParseFloat(ma.Groups["deg"].Value),
                });
                continue;
            }
            ma = ActivityCompass.Match(line);
            if (ma.Success) {
                var deg = -1f;
                foreach (var (word, d) in CompassWords) {
                    if (ma.Groups["dir"].Value == word) { deg = d; break; }
                }
                if (deg >= 0f) {
                    (current != null ? current.Constraints : doc.Items).Add(new ParsedItem {
                        RawLine = line, Kind = "bearing", AnchorText = ma.Groups["anchor"].Value.Trim(),
                        Value1 = deg, Fuzzy = true,
                    });
                    continue;
                }
                // 未收录的罗盘词：落入未解析日志取样
            }

            // 网格坐标行（锚点/直接定位）
            var m = GridRef.Match(line);
            if (m.Success) {
                var col = char.ToUpperInvariant(m.Groups["col"].Value[0]) - 'A';
                var gx = col + ParseFloat(m.Groups["sx"].Value) / 10f + SubGridCenter;
                var gy = ParseFloat(m.Groups["row"].Value) - 1f + ParseFloat(m.Groups["sy"].Value) / 10f + SubGridCenter;
                var refName = m.Groups["name"].Value.Trim();
                // "穆拉谷地 总站：J6 0:4"：列车时刻表的到达站，同时是移动目标解算的车站锚点
                if (refName.EndsWith("总站")) {
                    var tr = EnsureTrain(doc);
                    tr.StationName = refName;
                    tr.StationGx = gx;
                    tr.StationGy = gy;
                }
                doc.Items.Add(new ParsedItem {
                    RawLine = line, Kind = "gridref", AnchorText = refName,
                    Value1 = gx, Value2 = gy,
                    TokenName = GuessToken(line),
                });
                continue;
            }

            // 列车时刻表等时间行与定位无关，跳过（免得进"未解析"日志刷屏）
            if (line.Contains("T=")) continue;

            // 约束行（自…的方位角/距离，可一行两约束）
            var mb = BearingPart.Match(line);
            var md = DistancePart.Match(line);
            if (mb.Success || md.Success) {
                var target = current != null ? current.Constraints : doc.Items;
                if (mb.Success) {
                    var anchor = mb.Groups["anchor"].Value.Trim();
                    target.Add(new ParsedItem {
                        RawLine = line, Kind = "bearing", AnchorText = anchor,
                        Value1 = ParseFloat(mb.Groups["deg"].Value),
                    });
                    var mt = DistanceTail.Match(line, mb.Index + mb.Length);
                    if (mt.Success) {
                        target.Add(new ParsedItem {
                            RawLine = line, Kind = "distance", AnchorText = anchor,
                            Value1 = ToKm(mt),
                        });
                    }
                }
                else {
                    target.Add(new ParsedItem {
                        RawLine = line, Kind = "distance", AnchorText = md.Groups["anchor"].Value.Trim(),
                        Value1 = ToKm(md),
                    });
                }
                continue;
            }

            // 主题行
            m = SubjectHeader.Match(line);
            if (m.Success && !line.StartsWith("自")) {
                current = new IntelSubject {
                    Name = m.Groups["name"].Value.Trim(),
                    TokenName = GuessToken(line),
                };
                doc.Subjects.Add(current);
                continue;
            }

            // 标尺誊抄（锚点=炮位）
            m = AngleDistance.Match(line);
            if (m.Success) {
                doc.Items.Add(new ParsedItem {
                    RawLine = line, Kind = "angleDist", AnchorText = "",
                    Value1 = ParseFloat(m.Groups[1].Value), Value2 = ParseFloat(m.Groups[2].Value),
                });
                continue;
            }

            // ===== 紧急转移 / 后勤车队（位置报告卡）报文 =====
            // "铁巢新位置位于H4某处"：转移后的大格公告（自动车队卡的参数来源）
            m = TurretZone.Match(line);
            if (m.Success) {
                doc.Items.Add(new ParsedItem {
                    RawLine = line, Kind = "turretZone", AnchorText = m.Groups["cell"].Value,
                });
                continue;
            }
            // "测距仪显示我与铁巢相距0.61km：车队#2当前位置：H4 8:4"：铁巢的距离圆约束
            m = ConvoyDistance.Match(line);
            if (m.Success) {
                var (gx, gy) = ParseBareGrid(m, "");
                doc.Items.Add(new ParsedItem {
                    RawLine = line, Kind = "turretDist",
                    Value1 = ParseFloat(m.Groups["dist"].Value), Value2 = gx, Value3 = gy,
                });
                continue;
            }
            // "FO[#n] 音频报告 敌方观测员#10: 1.92km 自 F5 3:0"：前线观测员卡，敌军的距离圆约束
            m = FoReport.Match(line);
            if (m.Success) {
                var (gx, gy) = ParseBareGrid(m, "");
                doc.Items.Add(new ParsedItem {
                    RawLine = line, Kind = "foDist", AnchorText = m.Groups["name"].Value.Trim(),
                    Value1 = ToKm(m), Value2 = gx, Value3 = gy,
                    TokenName = GuessToken(line),
                });
                continue;
            }
            // "FO#2发现 敌方观测员#9: 313 自 G1 9:4"：前线观测员的方位线报告（敌军的方位线约束）。
            // 必须先于 ConvoyBearing 匹配，否则会被误吞成铁巢约束
            m = FoBearing.Match(line);
            if (m.Success) {
                var (gx, gy) = ParseBareGrid(m, "");
                doc.Items.Add(new ParsedItem {
                    RawLine = line, Kind = "foBearing", AnchorText = m.Groups["name"].Value.Trim(),
                    Value1 = ParseFloat(m.Groups["deg"].Value), Value2 = gx, Value3 = gy,
                    TokenName = GuessToken(line),
                });
                continue;
            }
            // "车队#3发现铁巢: … 180 自 H4 2:7"：铁巢的方位线约束
            m = ConvoyBearing.Match(line);
            if (m.Success) {
                var (gx, gy) = ParseBareGrid(m, "");
                doc.Items.Add(new ParsedItem {
                    RawLine = line, Kind = "turretBearing",
                    Value1 = ParseFloat(m.Groups["deg"].Value), Value2 = gx, Value3 = gy,
                });
                continue;
            }
            // "铁巢从I6 5:3转移至未知位置"：转移公告本身无定位价值（检测靠图标位移），静默跳过
            if (line.Contains("转移至未知位置")) continue;

            if (Regex.IsMatch(line, @"\d")) {
                MelonLogger.Msg($"[Intel] 未能解析的行({sourceTag}): {line}");
            }
        }
        return doc;
    }

    private static float ToKm(Match m) {
        var v = ParseFloat(m.Groups["dist"].Value);
        return m.Groups["unit"].Value is "米" or "m" ? v / 1000f : v;
    }

    private static TrainScheduleRaw EnsureTrain(IntelDocument doc) => doc.Train ??= new TrainScheduleRaw();

    private static string? GuessToken(string line) {
        var lower = line.ToLowerInvariant();
        foreach (var (keyword, token) in UnitKeywords) {
            if (lower.Contains(keyword)) return token;
        }
        return null;
    }

    private static float ParseFloat(string s) =>
        float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0f;
}
