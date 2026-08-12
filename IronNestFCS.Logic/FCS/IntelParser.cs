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
    public float Value3;           // turretDist/turretBearing：Value1=距离/角度, Value2=报点x, Value3=报点y
    public string? TokenName;      // 行内识别出的棋子类型名
}

/// <summary>一个情报主题（如"敌方炮兵指挥中心#1:"），其下的"自…"约束都归到它名下。</summary>
public sealed class IntelSubject {
    public string Name = "";
    public string? TokenName;
    public readonly List<ParsedItem> Constraints = new();
}

/// <summary>一段情报文本的解析结果：散条目（gridref 等）+ 有序主题列表。</summary>
public sealed class IntelDocument {
    public readonly List<ParsedItem> Items = new();
    public readonly List<IntelSubject> Subjects = new();

    public void Merge(IntelDocument other) {
        Items.AddRange(other.Items);
        Subjects.AddRange(other.Subjects);
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
    // 已验证：观测员#1 "M3 6:1" ↔ 实体 world (12.65, 2.15)。
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

    // "车队#3发现铁巢: <风味文本> 180 自 H4 2:7 . . ." —— 方位线约束（起点=报点，结构为"<度> 自 <格>"）
    private static readonly Regex ConvoyBearing =
        new(@"(?<deg>\d{1,3})\s*自\s*(?<col>[A-Z])(?<row>\d{1,2})\s+(?<sx>\d)\s*[:：]\s*(?<sy>\d)",
            RegexOptions.Compiled);

    /// <summary>裸网格坐标（无名称前缀，如 "H4 2:7"）→ 网格公里坐标，与 GridRef 行同一换算。</summary>
    private static (float gx, float gy) ParseBareGrid(Match m, string p) => (
        char.ToUpperInvariant(m.Groups[p + "col"].Value[0]) - 'A' + ParseFloat(m.Groups[p + "sx"].Value) / 10f,
        ParseFloat(m.Groups[p + "row"].Value) - 1f + ParseFloat(m.Groups[p + "sy"].Value) / 10f);

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
        foreach (var rawLine in text.Split('\n')) {
            var line = RichTag.Replace(rawLine, "").Trim();
            if (line.Length == 0 || line == "." || line == "- - -") continue;

            // 网格坐标行（锚点/直接定位）
            var m = GridRef.Match(line);
            if (m.Success) {
                var col = char.ToUpperInvariant(m.Groups["col"].Value[0]) - 'A';
                doc.Items.Add(new ParsedItem {
                    RawLine = line, Kind = "gridref", AnchorText = m.Groups["name"].Value.Trim(),
                    Value1 = col + ParseFloat(m.Groups["sx"].Value) / 10f,
                    Value2 = ParseFloat(m.Groups["row"].Value) - 1f + ParseFloat(m.Groups["sy"].Value) / 10f,
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
