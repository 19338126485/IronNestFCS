using System.Collections;
using Il2Cpp;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 情报/测绘系统：
///  - <see cref="Survey"/>：读两台电报机纸带 + 笔记本 → 解析（<see cref="IntelParser"/>）→
///    按主题逐组几何解算（<see cref="GeoSolver"/>）→ 候选位置。主题解出后注册为锚点，
///    供后续主题链式引用（实测：敌方防空炮以"敌方炮兵指挥中心#2"为锚点）。
///    锚点有多解歧义时下游主题对每个分支各解一次；图外交点直接剪除；
///    唯一匹配的友方实体坐标作为地面真值（锚点优先、主题多解确认），敌方实体绝不读取。
///  - 候选有生命周期：登记簿按主题名去重，重复 Survey 原地更新坐标、保留用户状态；
///    状态：待处理 / 已落子(✓) / 已忽略(✗)。Next 只在待处理里轮转，Del 忽略当前条。
///  - 每个待处理候选在地图桌面上有一个 3D 标记环 + 序号标签（程序网格，白色=正常，黄色=低置信度）。
///  - 自动刷新：每 3 秒对比情报文本哈希，变了自动重新解析（可用 Auto 按钮开关）。
///  - 手动落子跟随：检测到玩家把棋子拖到某候选 0.4km 内，自动标记该候选为已落子。
///  - <see cref="RevealAll"/>：【作弊】点亮全部地图实体视觉。
///
/// 坐标系（实测结论）：
///  - 网格坐标：实体 world 的 x/y 即网格公里（列=A+floor(x)，行=floor(y)+1，1 格=1km）。
///  - 网格 → 桌面：实体图标层是 3D 倾斜平面，用全部实体图标的运行时变换做
///    最小二乘仿射拟合（x/y/z 三分量，<see cref="FitAffine"/>），残差超差拒绝落子。
///
/// 重载安全：不注册 IL2CPP 类型；不持静态 IL2CPP 引用；协程经 FSC.RunTracked 登记；
/// 标记物销毁/引用清空都在 ShutDown 完成。
/// </summary>
public class IntelSystem {
    private static readonly string[] TurretAliases = { "铁巢", "Iron Nest", "turret" };

    private FSC? fcs;

    /// <summary>锚点名 → 首选网格坐标。来源：gridref 行 + 已解算主题（链式）。每次 Survey 重建。</summary>
    private readonly Dictionary<string, Vector2> anchors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>锚点名 → 全部候选坐标（多解歧义沿锚点链传播：下游主题对每个分支各解一次）。</summary>
    private readonly Dictionary<string, List<Vector2>> anchorOptions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本轮 Survey 产出过的登记簿 key（用于收回不再成立的 alt 假设）。</summary>
    private readonly HashSet<string> touchedKeys = new();
    private bool verbosePass; // 本次 Survey 是否详细日志（实体锚点匹配等例行输出只在其开启时打印）

    // ===== 候选登记簿（按主题名去重，跨 Survey 保留用户状态） =====
    private sealed class CandidateEntry {
        public int Seq;                    // 固定序号（地图标签与窗口行的对应关系）
        public string Key = "";            // 登记簿 key（主题名，alt 带 "(alt)" 后缀）
        public SurveyCandidate Cand = new();
        public bool Placed;
        public bool Ignored;
        public GameObject? Marker;
        public GameObject? Label;
    }
    private readonly Dictionary<string, CandidateEntry> registry = new();
    private readonly List<CandidateEntry> insertionOrder = new();
    private int seqCounter;

    /// <summary>当前待处理候选（未落子/未忽略），与 activeEntries 同步。UI 只读。</summary>
    public readonly List<SurveyCandidate> Candidates = new();
    private readonly List<CandidateEntry> activeEntries = new();
    public int CurrentIndex;
    public readonly List<string> WindowLines = new();

    // 棋子托盘：首次使用时快照各棋子的初始（托盘）位置；不在初始位 = 已被使用/移动。
    private readonly Dictionary<int, Vector3> tokenHome = new();
    private readonly HashSet<int> placedTokenIds = new();
    private bool tokenHomeCaptured;

    // 仿射校准：grid(km) → 实体图标层世界坐标（3D 倾斜平面，三个分量都拟合）。
    private bool affineReady;
    private float aX, bX, cX, aY, bY, cY, aZ, bZ, cZ;
    private float markerLocalPerKm = 1f; // 桌面 local 单位/km（FitAffine 时测算，给标记环定尺寸）

    // 自动刷新
    public bool AutoRefresh { get; private set; } = true;
    private float nextPollTime;
    private string lastIntelHash = "";
    private IntPtr lastMissionPtr;

    // 紧急转移：ConvoyAssist 检测到炮位移动后置位。期间纸带上的旧"铁巢"网格行不再可信；
    // 出现不同的新网格行（后勤车队情报到位）时自动解除。
    private bool turretRelocationPending;
    private Vector2 staleTurretGrid;
    /// <summary>转移状态对外只读（ConvoyAssist 的自动车队循环据此判断是否继续打卡）。</summary>
    public bool TurretRelocationPending => turretRelocationPending;
    /// <summary>最近一次解析到的"铁巢新位置位于X某处"大格公告（自动车队卡参数）。</summary>
    private string? lastTurretZoneCell;

    // 标记物资源（实例字段，ShutDown 清空；静态会钉住旧 ALC）
    private Mesh? ringMesh;
    private Material? markerMatNormal;
    private Material? markerMatLowConf;

    // 重型侦察 dump 每次会话只做一次（全场景 Transform 扫描会卡帧）。
    private bool diagDone;

    public void Bind(FSC fcs) => this.fcs = fcs;

    public void ShutDown() {
        foreach (var e in insertionOrder) DestroyVisuals(e);
        insertionOrder.Clear();
        registry.Clear();
        anchors.Clear();
        anchorOptions.Clear();
        Candidates.Clear();
        activeEntries.Clear();
        WindowLines.Clear();
        CurrentIndex = 0;
        tokenHome.Clear();
        placedTokenIds.Clear();
        tokenHomeCaptured = false;
        affineReady = false;
        diagDone = false;
        lastIntelHash = "";
        lastMissionPtr = IntPtr.Zero;
        ringMesh = null;
        markerMatNormal = null;
        markerMatLowConf = null;
        fcs = null;
    }

    // ================= 每帧驱动（FSC.Update 调用） =================

    private const float PollIntervalSeconds = 3f;

    public void Tick() {
        if (fcs == null) return;
        if (Time.realtimeSinceStartup < nextPollTime) return;
        nextPollTime = Time.realtimeSinceStartup + PollIntervalSeconds;

        // 换任务检测：FireMission 实例换了 → 清空登记簿与托盘快照
        var fm = FireMission.Instance;
        var ptr = fm == null ? IntPtr.Zero : fm.Pointer;
        if (ptr != lastMissionPtr) {
            lastMissionPtr = ptr;
            ResetMissionState();
        }

        if (fm == null) return;
        DetectManualPlacements();

        if (!AutoRefresh) return;
        var hash = ComputeIntelHash();
        if (hash.Length == 0 || hash == lastIntelHash) return;
        lastIntelHash = hash;
        MelonLogger.Msg("[Intel] 检测到情报更新，自动重新解析");
        Survey(full: false);
    }

    public bool ToggleAutoRefresh() {
        AutoRefresh = !AutoRefresh;
        MelonLogger.Msg($"[Intel] 自动刷新: {(AutoRefresh ? "开" : "关")}");
        return AutoRefresh;
    }

    private void ResetMissionState() {
        foreach (var e in insertionOrder) DestroyVisuals(e);
        insertionOrder.Clear();
        registry.Clear();
        anchors.Clear();
        anchorOptions.Clear();
        Candidates.Clear();
        activeEntries.Clear();
        CurrentIndex = 0;
        tokenHome.Clear();
        placedTokenIds.Clear();
        tokenHomeCaptured = false;
        affineReady = false;
        lastIntelHash = "";
        WindowLines.Clear();
        turretRelocationPending = false;
        lastTurretZoneCell = null;
    }

    /// <summary>
    /// ConvoyAssist 通报：铁巢已紧急转移。记下当前（即将过时的）炮位并立即重解析——
    /// 此后 TryGetTurretGrid 会拒绝与旧值相同的"铁巢"锚点，直到纸带出现新位置。
    /// </summary>
    public void OnTurretRelocated() {
        if (anchors.TryGetValue("铁巢", out var g)) staleTurretGrid = g;
        turretRelocationPending = true;
        MelonLogger.Msg("[Intel] 铁巢已转移：旧炮位锚点作废，等待新位置情报（后勤车队/新报文）");
        Survey(full: false);
    }

    // ================= 按钮 2：一键揭示全图（作弊） =================

    public void RevealAll() {
        if (fcs == null) { MelonLogger.Warning("[Intel] RevealAll: 未绑定"); return; }
        fcs.RunTracked(RevealAllRoutine());
    }

    private IEnumerator RevealAllRoutine() {
        MelonLogger.Msg("[Intel] RevealAll: start");
        for (var tick = 0; tick < 30; ++tick) {
            var total = 0;
            var newly = 0;
            foreach (var loc in AllEntityLocations()) {
                total++;
                try {
                    if (loc.VisualRoot != null && !loc.VisualRoot.activeSelf) {
                        loc.VisualRoot.SetActive(true);
                        newly++;
                    }
                    var info = loc.transform.FindChild("Info");
                    if (info != null && !info.gameObject.activeSelf) info.gameObject.SetActive(true);
                }
                catch (Exception ex) {
                    MelonLogger.Error($"[Intel] RevealAll tick failed: {ex.Message}");
                }
            }
            if (tick == 0 || newly > 0) {
                MelonLogger.Msg($"[Intel] RevealAll: tick {tick}, newly revealed {newly} / {total}");
            }
            yield return new WaitForSeconds(1f);
        }
        MelonLogger.Msg("[Intel] RevealAll: done");
    }

    private List<EntityLocation> AllEntityLocations() {
        var res = new List<EntityLocation>();
        var fm = FireMission.Instance;
        if (fm != null && fm.Entities != null) {
            foreach (var kv in fm.Entities) {
                if (kv.Value?.Location != null) res.Add(kv.Value.Location);
            }
        }
        if (res.Count == 0 && fcs != null) {
            res = fcs.MapTable.GetAllFireMissionEntities();
        }
        return res;
    }

    // ================= 按钮 1：自动测绘解析 =================

    public void Survey() => Survey(full: true);

    private void Survey(bool full) {
        verbosePass = full;
        if (full) {
            MelonLogger.Msg("[Intel] ==== Survey start ====");
            if (!diagDone) {
                DumpDiagnostics();
                diagDone = true;
            }
            // 卡牌侦察每次手动 Survey 都跑：卡牌状态随对局变化，需要覆盖打牌前后的快照
            fcs?.Convoy.DumpCardRecon();
        }

        anchors.Clear();
        anchorOptions.Clear();
        var doc = CollectIntel();
        if (full) MelonLogger.Msg($"[Intel] 解析: {doc.Items.Count} 条散条目, {doc.Subjects.Count} 个主题");

        var results = BuildCandidateResults(doc, full);
        FitAffine();
        EnsureTurretPiecePlaced();
        touchedKeys.Clear();
        SyncRegistry(results);
        RetractStaleAlts();
        SyncCandidates();
        RebuildWindowLines();

        // 自动后勤车队：转移未定位 + 大格已知 + 开关开 → 触发打卡循环（已在跑则忽略）
        if (turretRelocationPending && fcs != null && fcs.Convoy.AutoConvoy && !fcs.Convoy.NestGps) {
            if (lastTurretZoneCell != null) {
                fcs.Convoy.RequestConvoyIntel(lastTurretZoneCell);
            }
            else if (full) {
                MelonLogger.Msg("[Intel] 铁巢已转移但大格公告尚未出现，等报文打印后自动打车队卡");
            }
        }

        if (full) {
            foreach (var c in Candidates) {
                var conf = c.LowConfidence ? " [低置信度]" : "";
                MelonLogger.Msg($"[Intel] 候选 #{FindEntry(c)?.Seq}: [{c.Name}] ({c.Point.x:F2},{c.Point.y:F2}) " +
                                $"score={c.Score:F3}{conf} <= {c.Basis}");
            }
            MelonLogger.Msg($"[Intel] ==== Survey end: {Candidates.Count} 待处理 / " +
                            $"{CountPlaced()} 已落子 / {CountIgnored()} 已忽略 ====");
        }
    }

    private IntelDocument CollectIntel() {
        var doc = new IntelDocument();
        foreach (var (tag, text) in ReadAllIntelTexts()) {
            doc.Merge(IntelParser.Parse(text, tag));
        }
        return doc;
    }

    /// <summary>情报文本哈希：两台电报机纸带 + 笔记本全文的哈希拼合。空 = 没有任何情报。</summary>
    private string ComputeIntelHash() {
        try {
            var h = 0;
            var any = false;
            foreach (var (_, text) in ReadAllIntelTexts()) {
                if (string.IsNullOrEmpty(text)) continue;
                any = true;
                h = h * 31 + text.GetHashCode();
            }
            return any ? h.ToString() : "";
        }
        catch (Exception ex) {
            MelonLogger.Error($"[Intel] 计算情报哈希失败: {ex.Message}");
            return lastIntelHash;
        }
    }

    private List<(string tag, string text)> ReadAllIntelTexts() {
        var res = new List<(string, string)>();
        try {
            foreach (var section in Object.FindObjectsOfType<NotepadSection>()) {
                if (section == null) continue;
                var txt = section.TargetText != null ? section.TargetText.text : "";
                if (!string.IsNullOrWhiteSpace(txt)) res.Add(($"Notepad:{section.UnityTag}", txt));
            }
        }
        catch (Exception ex) { MelonLogger.Error($"[Intel] 读笔记本失败: {ex.Message}"); }

        foreach (Teleprinter.Teleprinters type in Enum.GetValues(typeof(Teleprinter.Teleprinters))) {
            try {
                var tp = Teleprinter.GetTeleprinter(type);
                if (tp == null || tp.paperTransform == null) continue;
                // 整条纸带是单个 TMP_Text（私有 _tmp，挂在 paperTransform 上），不是逐行子物体。
                var tmp = tp.paperTransform.GetComponent<TMP_Text>()
                          ?? tp.paperTransform.GetComponentInChildren<TMP_Text>();
                var text = tmp != null ? tmp.text : null;
                if (!string.IsNullOrWhiteSpace(text)) {
                    res.Add(($"Teleprinter:{type}", text));
                    continue;
                }
                // 兜底：结构不符时按子物体逐行拼
                var sb = new System.Text.StringBuilder();
                for (var i = 0; i < tp.paperTransform.childCount; ++i) {
                    var lineTmp = tp.paperTransform.GetChild(i).GetComponentInChildren<TMP_Text>();
                    if (lineTmp != null && !string.IsNullOrWhiteSpace(lineTmp.text)) sb.AppendLine(lineTmp.text);
                }
                if (sb.Length > 0) res.Add(($"Teleprinter:{type}", sb.ToString()));
            }
            catch (Exception ex) { MelonLogger.Error($"[Intel] 读电报机 {type} 失败: {ex.Message}"); }
        }
        return res;
    }

    /// <summary>
    /// 三阶段解算，产出有序候选列表（不写登记簿）：
    /// 1) gridref 行 → 锚点字典（注册全名+末词别名；指向敌方的行同时成为直接候选）；
    /// 2) 主题按报文顺序逐组解算，解出即注册为锚点（链式）。锚点有多解歧义时，
    ///    下游主题对每个分支各解一次（2026-08 实测教训：住宅线圆双解选错支，
    ///    下游巡洋舰的真解就落在另一支推出的第三个交点上）；图外交点必为假解，剪除；
    ///    主题名唯一匹配友方实体时以实体坐标为地面真值，吸附最近分支并丢弃其余；
    /// 3) 散条目：angleDist 直接定点（锚点=炮位），零散 bearing/distance 汇总投票。
    /// </summary>
    private List<SurveyCandidate> BuildCandidateResults(IntelDocument doc, bool verbose) {
        var results = new List<SurveyCandidate>();

        // 阶段 1：网格坐标行
        foreach (var item in doc.Items) {
            if (item.Kind != "gridref") continue;
            var pos = new Vector2(item.Value1, item.Value2);
            RegisterAnchor(item.AnchorText, pos);

            var isEnemy = IsEnemyName(item.AnchorText, out var matchedEntity);
            if (isEnemy || (matchedEntity == null && item.TokenName != null)) {
                results.Add(new SurveyCandidate {
                    Name = item.AnchorText,
                    Point = pos,
                    Score = 0f,
                    Basis = $"grid note: {item.RawLine}",
                    TokenName = item.TokenName,
                });
            }
        }

        // 阶段 2：主题（有序，链式锚点，多解分支传播）
        foreach (var subject in doc.Subjects) {
            // 按"不同锚点名"收集各锚点的分支选项（同一锚点被多条约束引用时分支必须一致）
            var items = new List<ParsedItem>();
            var anchorNames = new List<string>();
            var optionsByAnchor = new Dictionary<string, List<Vector2>>();
            foreach (var item in subject.Constraints) {
                if (item.Kind != "bearing" && item.Kind != "distance") continue;
                if (!TryGetAnchorOptions(item.AnchorText, out var opts)) {
                    MelonLogger.Warning($"[Intel] 无法解析锚点 '{item.AnchorText}'（行: {item.RawLine}）");
                    continue;
                }
                items.Add(item);
                if (!optionsByAnchor.ContainsKey(item.AnchorText)) {
                    optionsByAnchor[item.AnchorText] = opts;
                    anchorNames.Add(item.AnchorText);
                }
            }
            if (items.Count < 2) {
                if (verbose && items.Count == 1)
                    MelonLogger.Msg($"[Intel] 主题 '{subject.Name}': 约束不足（1 条），无法定位");
                continue;
            }

            // 分支组合（上限 8，防组合爆炸；超出时优先保留各锚点首选分支）
            var combos = new List<List<Vector2>> { new() };
            foreach (var an in anchorNames) {
                var next = new List<List<Vector2>>();
                foreach (var combo in combos) {
                    foreach (var opt in optionsByAnchor[an]) {
                        var branch = new List<Vector2>(combo) { opt };
                        next.Add(branch);
                        if (next.Count >= 8) break;
                    }
                    if (next.Count >= 8) break;
                }
                combos = next;
            }

            var merged = new List<SurveyCandidate>();
            foreach (var combo in combos) {
                var posByAnchor = new Dictionary<string, Vector2>();
                for (var k = 0; k < anchorNames.Count; ++k) posByAnchor[anchorNames[k]] = combo[k];
                var constraints = new List<IGeoConstraint>();
                foreach (var item in items) {
                    var p = posByAnchor[item.AnchorText];
                    var name = string.IsNullOrWhiteSpace(item.AnchorText) ? "Turret" : item.AnchorText;
                    constraints.Add(item.Kind == "bearing"
                        ? new BearingLine { Origin = p, BearingDeg = item.Value1, AnchorName = name }
                        : new DistanceCircle { Center = p, RadiusKm = item.Value1, AnchorName = name });
                }
                if (verbose) {
                    foreach (var c in constraints) MelonLogger.Msg($"[Intel] 约束[{subject.Name}]: {c.Describe()}");
                }
                foreach (var s in GeoSolver.Solve(constraints)) {
                    var dup = false;
                    foreach (var m in merged) {
                        if (Vector2.Distance(m.Point, s.Point) < 0.05f) { dup = true; break; }
                    }
                    if (!dup) merged.Add(s);
                }
            }

            // 图外剪枝：越出地图的交点必为假解（实测远支曾解出 y=16.9 的图外点）
            var beforePrune = merged.Count;
            merged.RemoveAll(c => !IsOnMap(c.Point));
            if (verbose && merged.Count < beforePrune) {
                MelonLogger.Msg($"[Intel] 主题 '{subject.Name}': 剪除 {beforePrune - merged.Count} 个图外假解");
            }
            if (merged.Count == 0) {
                if (verbose) MelonLogger.Msg($"[Intel] 主题 '{subject.Name}': 所有交点均在图外，无法定位");
                continue;
            }
            merged.Sort((x, y) => x.Score.CompareTo(y.Score));

            // 友方实体确认：主题名唯一匹配友方实体 → 实体坐标是地面真值，
            // 吸附最近分支（报文整数度舍入归零），其余分支直接丢弃
            var confirm = FindUniqueFriendlyEntity(subject.Name);
            if (confirm != null) {
                var truth = WorldToGrid(confirm.Position);
                SurveyCandidate? nearest = null;
                var nd = float.MaxValue;
                foreach (var c in merged) {
                    var d = Vector2.Distance(c.Point, truth);
                    if (d < nd) { nd = d; nearest = c; }
                }
                if (nearest != null && nd <= 1f) {
                    nearest.Point = truth;
                    nearest.Score = 0f;
                    nearest.Basis += $" | 实体确认 {confirm.ID}";
                    merged.Clear();
                    merged.Add(nearest);
                    if (verbose) {
                        MelonLogger.Msg($"[Intel] 主题 '{subject.Name}' 经友方实体 {confirm.ID} 确认 " +
                                        $"@ ({truth.x:F2},{truth.y:F2})，多解歧义已消除");
                    }
                }
                else if (verbose) {
                    MelonLogger.Msg($"[Intel] 主题 '{subject.Name}': 实体 {confirm.ID} 位置与任何解都不符，保留几何解");
                }
            }

            var best = merged[0];
            best.Name = subject.Name;
            best.TokenName ??= subject.TokenName;
            results.Add(best);
            var emitted = new List<Vector2> { best.Point };

            // 多解歧义：分数接近首选的分支一并列出（(alt)/(alt2)/(alt3)，上限 4 个），
            // 由 Next/Place 人工裁决；同时全部注册为锚点分支供下游主题枚举
            var altCount = 0;
            for (var i = 1; i < merged.Count; ++i) {
                if (merged[i].Score - best.Score >= GeoSolver.AmbiguityThresholdKm) break;
                if (altCount >= 3) {
                    if (verbose) MelonLogger.Msg($"[Intel] 主题 '{subject.Name}': 歧义分支过多，其余省略");
                    break;
                }
                var alt = merged[i];
                alt.Name = subject.Name + (altCount == 0 ? "(alt)" : $"(alt{altCount + 1})");
                alt.TokenName ??= subject.TokenName;
                results.Add(alt);
                emitted.Add(alt.Point);
                altCount++;
                if (verbose) {
                    MelonLogger.Msg($"[Intel] 主题 '{subject.Name}' 存在多解歧义: " +
                                    $"备选 ({alt.Point.x:F2},{alt.Point.y:F2}) score={alt.Score:F3}");
                }
            }
            RegisterAnchorOptions(subject.Name, emitted);
        }

        // 阶段 2.5：后勤车队（位置报告卡）报文 → 铁巢新位置解算。
        // 每条报告是对铁巢的一条几何约束（距离圆/方位线，锚点坐标行内自带）；
        // 唯一解才接受（双解歧义时等更多报告，由 AutoConvoy 继续打卡）；接受后注册为
        // "铁巢"锚点——TryGetTurretGrid 发现与旧值不同会自动解除转移状态、补摆棋子。
        string? zoneCell = null;
        var turretCons = new List<IGeoConstraint>();
        foreach (var item in doc.Items) {
            switch (item.Kind) {
                case "turretZone":
                    zoneCell = item.AnchorText;
                    break;
                case "turretDist":
                    turretCons.Add(new DistanceCircle {
                        Center = new Vector2(item.Value2, item.Value3), RadiusKm = item.Value1,
                        AnchorName = "车队报点",
                    });
                    break;
                case "turretBearing":
                    turretCons.Add(new BearingLine {
                        Origin = new Vector2(item.Value2, item.Value3), BearingDeg = item.Value1,
                        AnchorName = "车队报点",
                    });
                    break;
            }
        }
        lastTurretZoneCell = zoneCell ?? lastTurretZoneCell;
        if (turretRelocationPending && turretCons.Count >= 2) {
            var solved = GeoSolver.Solve(turretCons);
            solved.RemoveAll(c => !IsOnMap(c.Point));
            if (solved.Count == 0) {
                if (verbose) MelonLogger.Msg("[Intel] 车队情报解算：交点均在图外，等待更多报告");
            }
            else {
                var best = solved[0];
                // 真歧义 = 存在"分数接近且位置远离"的另一支。车队距离圆常近乎相切，
                // 会在真解旁边蹭出一簇仅差几十分米的近点——那不算歧义，直接采信最优解。
                var ambiguous = false;
                for (var i = 1; i < solved.Count; ++i) {
                    if (solved[i].Score - best.Score >= GeoSolver.AmbiguityThresholdKm) break;
                    if (Vector2.Distance(solved[i].Point, best.Point) > 0.5f) { ambiguous = true; break; }
                }
                if (ambiguous) {
                    if (verbose) {
                        MelonLogger.Msg($"[Intel] 车队情报仍有双解歧义: ({best.Point.x:F2},{best.Point.y:F2}) 等 " +
                                        $"{solved.Count} 个分支，需要更多报告");
                    }
                }
                else {
                    RegisterAnchor("铁巢", best.Point);
                    MelonLogger.Msg($"[Intel] 车队情报解出铁巢新位置: ({best.Point.x:F2},{best.Point.y:F2}) " +
                                    $"score={best.Score:F3}（{turretCons.Count} 条约束）");
                }
            }
        }

        // 阶段 3：散条目
        var loose = new List<IGeoConstraint>();
        foreach (var item in doc.Items) {
            switch (item.Kind) {
                case "angleDist": {
                    if (!TryGetTurretGrid(out var origin)) {
                        MelonLogger.Warning("[Intel] angleDist 条目需要炮位锚点，但炮位未知");
                        break;
                    }
                    var dir = new Vector2(Mathf.Sin(item.Value1 * Mathf.Deg2Rad),
                                          Mathf.Cos(item.Value1 * Mathf.Deg2Rad));
                    var point = origin + dir * item.Value2;
                    if (!IsOnMap(point)) {
                        if (verbose) MelonLogger.Msg($"[Intel] 誊抄落点在图外，跳过: {item.RawLine}");
                        break;
                    }
                    results.Add(new SurveyCandidate {
                        Name = "marker:" + item.RawLine,
                        Point = point,
                        Score = 0f,
                        Basis = $"marker note: {item.RawLine}",
                        TokenName = item.TokenName,
                    });
                    break;
                }
                case "bearing":
                case "distance": {
                    var c = ResolveConstraint(item);
                    if (c != null) loose.Add(c);
                    break;
                }
            }
        }
        if (loose.Count > 0) {
            foreach (var s in GeoSolver.Solve(loose)) {
                if (!IsOnMap(s.Point)) continue;
                s.Name = "loose:" + s.Basis;
                results.Add(s);
            }
        }
        return results;
    }

    // 地图边界（km）：全任务地图 20×10（列 A–T、行 1–10），留 0.5km 边距。
    private static bool IsOnMap(Vector2 p) =>
        p.x is >= -0.5f and <= 20.5f && p.y is >= -0.5f and <= 10.5f;

    /// <summary>
    /// 把本次解算结果同步进登记簿：同名条目原地更新（保留 已落子/已忽略 状态），
    /// 新条目登记并生成地图标记。情报纸带保留历史，消失的条目不清除（新任务由 ResetMissionState 处理）。
    /// 图外结果直接跳过（防御性；正常路径在 BuildCandidateResults 已剪枝）。
    /// </summary>
    private void SyncRegistry(List<SurveyCandidate> results) {
        foreach (var r in results) {
            var key = string.IsNullOrEmpty(r.Name) ? r.Basis : r.Name;
            if (!IsOnMap(r.Point)) {
                MelonLogger.Msg($"[Intel] 跳过图外候选: [{key}] ({r.Point.x:F2},{r.Point.y:F2})");
                continue;
            }
            touchedKeys.Add(key);
            if (registry.TryGetValue(key, out var e)) {
                var old = e.Cand.Point;
                var moved = Vector2.Distance(old, r.Point) > 0.01f;
                e.Cand.Point = r.Point;
                e.Cand.Score = r.Score;
                e.Cand.Basis = r.Basis;
                e.Cand.LowConfidence = r.LowConfidence;
                e.Cand.TokenName ??= r.TokenName;
                if (moved) {
                    if (e.Marker != null) PositionMarker(e);
                    if (Vector2.Distance(old, r.Point) > 0.1f) {
                        // 显著修正必须可见：之前"仅已落子才记录"导致锚点确认后的静默大跳无人察觉
                        MelonLogger.Msg($"[Intel] 情报修正: [{e.Cand.Name}] ({old.x:F2},{old.y:F2}) → " +
                                        $"({r.Point.x:F2},{r.Point.y:F2})" +
                                        (e.Placed ? "（棋子不会自动跟着动，需要请手动调整）" : ""));
                    }
                }
            }
            else {
                e = new CandidateEntry { Cand = r, Seq = ++seqCounter, Key = key };
                registry[key] = e;
                insertionOrder.Add(e);
                CreateMarkerVisuals(e);
                MelonLogger.Msg($"[Intel] 新目标 #{e.Seq}: [{r.Name}] ({r.Point.x:F2},{r.Point.y:F2})" +
                                $"{(r.LowConfidence ? " [低置信度]" : "")}");
            }
        }
    }

    /// <summary>
    /// 收回陈旧备选：本轮解算不再出现的 (alt) 假设视为已被排除（锚点被实体确认、
    /// 或分支随新情报消失），自动忽略并撤下标记；已落子的保留（棋子是玩家的决定）。
    /// </summary>
    private void RetractStaleAlts() {
        foreach (var e in insertionOrder) {
            if (e.Placed || e.Ignored) continue;
            if (!e.Key.Contains("(alt")) continue;
            if (touchedKeys.Contains(e.Key)) continue;
            e.Ignored = true;
            DestroyVisuals(e);
            MelonLogger.Msg($"[Intel] 备选假设被收回: #{e.Seq} [{e.Cand.Name}]");
        }
    }

    /// <summary>重建待处理列表（未落子且未忽略），保持登记顺序。</summary>
    private void SyncCandidates() {
        Candidates.Clear();
        activeEntries.Clear();
        foreach (var e in insertionOrder) {
            if (e.Placed || e.Ignored) continue;
            Candidates.Add(e.Cand);
            activeEntries.Add(e);
        }
        if (CurrentIndex >= Candidates.Count) CurrentIndex = 0;
    }

    private CandidateEntry? FindEntry(SurveyCandidate c) {
        foreach (var e in insertionOrder) if (ReferenceEquals(e.Cand, c)) return e;
        return null;
    }

    private int CountPlaced() {
        var n = 0;
        foreach (var e in insertionOrder) if (e.Placed) n++;
        return n;
    }

    private int CountIgnored() {
        var n = 0;
        foreach (var e in insertionOrder) if (e.Ignored) n++;
        return n;
    }

    /// <summary>把解析条目翻译成几何约束（取锚点首选分支）；失败打日志并返回 null。</summary>
    private IGeoConstraint? ResolveConstraint(ParsedItem item) {
        if (!TryGetAnchorOptions(item.AnchorText, out var opts)) {
            MelonLogger.Warning($"[Intel] 无法解析锚点 '{item.AnchorText}'（行: {item.RawLine}）");
            return null;
        }
        var anchor = opts[0];
        var name = string.IsNullOrWhiteSpace(item.AnchorText) ? "Turret" : item.AnchorText;
        return item.Kind switch {
            "bearing" => new BearingLine { Origin = anchor, BearingDeg = item.Value1, AnchorName = name },
            "distance" => new DistanceCircle { Center = anchor, RadiusKm = item.Value1, AnchorName = name },
            _ => null,
        };
    }

    private void RegisterAnchor(string name, Vector2 pos) =>
        RegisterAnchorOptions(name, new List<Vector2> { pos });

    private void RegisterAnchorOptions(string name, List<Vector2> positions) {
        if (string.IsNullOrWhiteSpace(name) || positions.Count == 0) return;
        anchors[name] = positions[0];
        anchorOptions[name] = positions;
        // 长名注册末词别名："卡斯特尔德费尔斯海滩 总站" → "总站"
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1 && !anchorOptions.ContainsKey(parts[^1])) {
            anchors[parts[^1]] = positions[0];
            anchorOptions[parts[^1]] = positions;
        }
    }

    // ================= 候选操作（Next / Place / Del） =================

    /// <summary>在待处理候选间轮转。</summary>
    public void CycleCandidate() {
        if (Candidates.Count == 0) {
            MelonLogger.Msg("[Intel] 没有待处理的候选（先 Survey，或全部已处理）");
            return;
        }
        CurrentIndex = (CurrentIndex + 1) % Candidates.Count;
        var c = Candidates[CurrentIndex];
        MelonLogger.Msg($"[Intel] 当前候选 #{activeEntries[CurrentIndex].Seq}: [{c.Name}] " +
                        $"({c.Point.x:F2},{c.Point.y:F2}) score={c.Score:F3}");
        RebuildWindowLines();
    }

    /// <summary>把当前候选位置放上对应类型的棋子，并标记为已落子（撤下地图标记）。</summary>
    public void PlaceCurrentCandidate() {
        if (Candidates.Count == 0 || CurrentIndex >= activeEntries.Count) {
            MelonLogger.Msg("[Intel] 没有可放置的候选");
            return;
        }
        var e = activeEntries[CurrentIndex];
        var c = e.Cand;
        if (c.LowConfidence) {
            MelonLogger.Warning($"[Intel] 注意：候选 [{c.Name}] 是低置信度解（约束近乎相切），落点可能明显偏离");
        }
        var tokenName = c.TokenName ?? "MapToken_Artillery";
        if (PlaceTokenAt(tokenName, c.Point)) {
            e.Placed = true;
            DestroyVisuals(e);
            SyncCandidates();
            RebuildWindowLines();
        }
    }

    /// <summary>忽略当前候选（Del）：从待处理移除并撤下地图标记，重复 Survey 不会复活。</summary>
    public void DismissCurrent() {
        if (Candidates.Count == 0 || CurrentIndex >= activeEntries.Count) {
            MelonLogger.Msg("[Intel] 没有可忽略的候选");
            return;
        }
        var e = activeEntries[CurrentIndex];
        e.Ignored = true;
        DestroyVisuals(e);
        MelonLogger.Msg($"[Intel] 已忽略 #{e.Seq}: [{e.Cand.Name}]");
        SyncCandidates();
        RebuildWindowLines();
    }

    /// <summary>
    /// 直接对当前候选开火（Fire）：不经过棋子——用仿射把网格坐标换成桌面局部坐标，
    /// 再走与 MapTable.GetMarkTarget（T 按钮）完全相同的相对测量公式生成射击诸元入队。
    /// 与"放棋子再按 T"数学等价，但不受棋子编号 1~4 的限制，目标数无上限。
    /// 入队后自动切到下一个待处理候选，可连续点名。
    /// </summary>
    public void FireCurrentCandidate() {
        if (Candidates.Count == 0 || CurrentIndex >= activeEntries.Count) {
            MelonLogger.Msg("[Intel] 没有可开火的候选");
            return;
        }
        if (!affineReady) {
            MelonLogger.Error("[Intel] 仿射校准不可用，无法生成射击诸元（先按 Survey）");
            return;
        }
        var surface = GameObject.Find("Draggable Surface")?.transform;
        var turretLocal = fcs?.MapTable.TurretLocalPos();
        if (surface == null || turretLocal == null || fcs == null) {
            MelonLogger.Error("[Intel] 开火失败：地图或炮塔未绑定（FCS 未就绪？）");
            return;
        }
        var e = activeEntries[CurrentIndex];
        // 与 GetMarkTarget 相同的公式：局部坐标差 → 距离×3.8164 / 方向角 SignedAngle
        var targetLocal = surface.InverseTransformPoint(AffineForward(e.Cand.Point));
        var delta = targetLocal - turretLocal.Value;
        var dist = delta.magnitude * 3.8164f;
        var angle = Vector3.SignedAngle(delta, Vector3.up, Vector3.forward);
        if (angle < 0) angle += 360;
        var task = new ArtilleryTask {
            targetId = e.Seq,
            angel = angle,
            distance = dist,
            position = new Vector3(e.Cand.Point.x, e.Cand.Point.y, 0f),
            bulletType = fcs.Interactor.selectedBulletType,
        };
        MelonLogger.Msg($"[Intel] 直接开火 #{e.Seq}: [{e.Cand.Name}] {angle:F1}°/{dist:F2}km {task.bulletType}");
        fcs.EnqueueTask(task);
        // 自动切到下一个待处理候选，便于连续点名
        if (Candidates.Count > 1) {
            CurrentIndex = (CurrentIndex + 1) % Candidates.Count;
            RebuildWindowLines();
        }
    }

    /// <summary>手动落子跟随：玩家把棋子拖到某待处理候选 0.4km 内 → 自动标记已落子。</summary>
    private void DetectManualPlacements() {
        if (!affineReady || activeEntries.Count == 0) return;
        var surface = GameObject.Find("Draggable Surface")?.transform;
        if (surface == null) return;
        if (!tokenHomeCaptured) CaptureTokenHomes(surface);

        var changed = false;
        for (var i = 0; i < surface.childCount; ++i) {
            var t = surface.GetChild(i);
            if (!t.name.StartsWith("MapToken_")) continue;
            var id = t.GetInstanceID();
            if (placedTokenIds.Contains(id)) continue;
            if (!tokenHome.TryGetValue(id, out var home)) continue;
            if (Vector3.Distance(home, t.localPosition) <= 0.02f) continue; // 没动过

            var grid = AffineInverse(surface.TransformPoint(t.localPosition));
            CandidateEntry? best = null;
            var bestDist = 0.15f; // 手动落子认定半径（km）：一小格=0.1km，1.5 小格，避免多目标混淆
            foreach (var e in activeEntries) {
                var d = Vector2.Distance(grid, e.Cand.Point);
                if (d < bestDist) { bestDist = d; best = e; }
            }
            if (best != null) {
                best.Placed = true;
                placedTokenIds.Add(id);
                DestroyVisuals(best);
                MelonLogger.Msg($"[Intel] 检测到手动落子: {t.name} → [{best.Cand.Name}]（偏差 {bestDist:F2}km），已标记");
                changed = true;
            }
        }
        if (changed) {
            SyncCandidates();
            RebuildWindowLines();
        }
    }

    /// <summary>
    /// 找一个还停在托盘初始位的闲置棋子，移到指定网格坐标对应的桌面位置。
    /// 网格 → 桌面走仿射校准（<see cref="FitAffine"/>）；校准不可用则拒绝落子。
    /// </summary>
    public bool PlaceTokenAt(string tokenName, Vector2 grid) {
        if (!affineReady) {
            MelonLogger.Error("[Intel] 仿射校准不可用（实体图标层拟合失败/未运行），拒绝落子。请先按 Survey。");
            return false;
        }
        var surface = GameObject.Find("Draggable Surface")?.transform;
        if (surface == null) {
            MelonLogger.Error("[Intel] 未找到 Draggable Surface，无法落子");
            return false;
        }
        if (!tokenHomeCaptured) CaptureTokenHomes(surface);

        Transform? freeToken = null;
        for (var i = 0; i < surface.childCount; ++i) {
            var t = surface.GetChild(i);
            if (t.name != tokenName && !t.name.StartsWith(tokenName + " ")) continue;
            var id = t.GetInstanceID();
            if (placedTokenIds.Contains(id)) continue;
            if (tokenHome.TryGetValue(id, out var home) &&
                Vector3.Distance(home, t.localPosition) > 0.02f) continue; // 已被移动
            freeToken = t;
            break;
        }
        if (freeToken == null) {
            MelonLogger.Warning($"[Intel] 没有闲置的 {tokenName} 棋子");
            return false;
        }

        var world = AffineForward(grid); // 图标平面上的完整 3D 点（z 由拟合给出）
        var local = surface.InverseTransformPoint(world);
        var lp = freeToken.localPosition;
        freeToken.localPosition = new Vector3(local.x, local.y, lp.z);
        placedTokenIds.Add(freeToken.GetInstanceID());
        MelonLogger.Msg($"[Intel] 已放置 {tokenName}（编号 '{GetTokenLabel(freeToken)}'）" +
                        $" @ grid({grid.x:F2},{grid.y:F2}) → local({local.x:F3},{local.y:F3})");
        return true;
    }

    /// <summary>
    /// 自动落炮塔棋子（每次 Survey 末尾执行，幂等）：报文"铁巢"网格行是每个任务必报的真值，
    /// 而棋子通常停在托盘默认位——此时 T1~T4 按钮与 Fire 的相对测量（local 差值公式）全部失真。
    /// 偏差 >0.05km 才移动；玩家已手动摆好（误差在报文 0.1km 舍入范围内）则不打扰。
    /// </summary>
    private void EnsureTurretPiecePlaced() {
        if (!affineReady) return;
        // 只用真值来源（NestGps 直读 / 报文网格行），不能用棋子自身位置兜底（那会恒等空转）；
        // 转移后旧锚点被 TryGetTurretGrid 拒绝，此处自然暂停，等新情报到位自动补摆。
        if (!TryGetTurretGrid(out var grid, allowPieceFallback: false)) {
            if (turretRelocationPending && verbosePass)
                MelonLogger.Msg("[Intel] 铁巢已转移且新位置未知，炮塔棋子暂不自动摆放");
            return;
        }
        var surface = GameObject.Find("Draggable Surface")?.transform;
        var piece = GameObject.Find("Player Turret Piece")?.transform;
        if (surface == null || piece == null) return;

        var currentGrid = AffineInverse(piece.position);
        if (Vector2.Distance(currentGrid, grid) <= 0.05f) return; // 已在正确位置

        var local = surface.InverseTransformPoint(AffineForward(grid));
        piece.localPosition = new Vector3(local.x, local.y, piece.localPosition.z);
        MelonLogger.Msg($"[Intel] 已自动放置铁巢棋子: ({currentGrid.x:F2},{currentGrid.y:F2}) → " +
                        $"grid({grid.x:F2},{grid.y:F2})（T1~T4/Fire 解算基准已校准）");
    }

    private void CaptureTokenHomes(Transform surface) {
        tokenHome.Clear();
        for (var i = 0; i < surface.childCount; ++i) {
            var t = surface.GetChild(i);
            if (t.name.StartsWith("MapToken_")) {
                tokenHome[t.GetInstanceID()] = t.localPosition;
            }
        }
        tokenHomeCaptured = true;
        MelonLogger.Msg($"[Intel] 棋子托盘快照: {tokenHome.Count} 枚");
    }

    private static string GetTokenLabel(Transform token) {
        var tmp = token.GetComponentInChildren<TMP_Text>();
        return tmp != null ? tmp.text : "";
    }

    // ================= 地图标记（3D 环 + 序号标签） =================

    private const float MarkerRadiusKm = 0.3f;

    private void CreateMarkerVisuals(CandidateEntry e) {
        if (!affineReady || fcs == null) return;
        var surface = GameObject.Find("Draggable Surface")?.transform;
        if (surface == null) return;
        try {
            var go = new GameObject($"FcsMarker#{e.Seq}");
            go.transform.SetParent(surface, false);
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = GetRingMesh();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = GetMarkerMaterial(e.Cand.LowConfidence);

            // 中心标杆：细圆柱穿透地图平面，两面都可见（环万一贴到背面也至少看得见杆）
            var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            var poleCollider = pole.GetComponent<Collider>();
            if (poleCollider != null) Object.Destroy(poleCollider); // 不挡准星射线
            pole.transform.SetParent(go.transform, false);
            pole.transform.localRotation = Quaternion.Euler(90f, 0f, 0f); // 圆柱 Y 轴 → local Z（穿透图面）
            pole.transform.localPosition = Vector3.zero;
            pole.transform.localScale = new Vector3(0.10f, 0.6f, 0.10f); // 相对环半径
            FcsSceneInteractor.SetColor(pole, e.Cand.LowConfidence ? Color.yellow : Color.white);

            e.Marker = go;
            PositionMarker(e); // 先就位定标，后面的标签缩放要用 lossyScale

            // 序号标签（ASCII，避开中文字体问题）。
            // 朝向/缩放不猜：直接照抄游戏自己的棋子数字标签（MapToken_* 下的 TMP）——
            //  TMP 文字单面渲染，朝向错了就"沉到桌子背面"（上一轮的 bug）。
            var label = fcs.Interactor.AddText($"#{e.Seq}", 1.2f);
            var refTmp = FindTokenLabelReference(surface);
            label.transform.SetParent(go.transform, false);
            label.transform.localPosition = new Vector3(1.2f, 0f, -0.15f);
            if (refTmp != null) {
                label.transform.rotation = refTmp.transform.rotation;
                var s = refTmp.transform.lossyScale.x / Mathf.Max(go.transform.lossyScale.x, 1e-6f);
                label.transform.localScale = Vector3.one * s;
                var tmp = label.GetComponent<TMP_Text>();
                if (tmp != null) tmp.fontSize = refTmp.fontSize;
            }
            else {
                label.transform.localRotation = Quaternion.identity;
            }

            e.Label = label;
            fcs.Interactor.TrackForShutdown(go); // 热重载时随场景交互器统一销毁
        }
        catch (Exception ex) {
            MelonLogger.Error($"[Intel] 创建标记失败: {ex.Message}");
        }
    }

    /// <summary>找游戏棋子上的数字标签（TMP）作朝向/缩放参照。</summary>
    private static TMP_Text? FindTokenLabelReference(Transform surface) {
        for (var i = 0; i < surface.childCount; ++i) {
            var t = surface.GetChild(i);
            if (!t.name.StartsWith("MapToken_")) continue;
            var tmp = t.GetComponentInChildren<TMP_Text>();
            if (tmp != null) return tmp;
        }
        return null;
    }

    private void PositionMarker(CandidateEntry e) {
        if (e.Marker == null) return;
        var surface = GameObject.Find("Draggable Surface")?.transform;
        if (surface == null || !affineReady) return;
        var world = AffineForward(e.Cand.Point);
        e.Marker.transform.position = world;
        e.Marker.transform.rotation = surface.rotation; // 环平面（local XY）贴合图面
        // localScale 以 surface 局部单位计：网格半径 1 × s，故 s = 期望半径(km) × 局部单位/km
        e.Marker.transform.localScale = Vector3.one * (MarkerRadiusKm * markerLocalPerKm);
    }

    private void DestroyVisuals(CandidateEntry e) {
        try {
            if (e.Marker != null) Object.Destroy(e.Marker); // 标签是 Marker 的子物体，随毁
            e.Marker = null;
            e.Label = null;
        }
        catch (Exception ex) {
            MelonLogger.Error($"[Intel] 销毁标记失败: {ex.Message}");
        }
    }

    private Mesh GetRingMesh() {
        if (ringMesh != null) return ringMesh;
        const int seg = 48;
        const float inner = 0.75f, outer = 1f;
        var verts = new Vector3[(seg + 1) * 2];
        // 双面渲染：正反两套三角形，免去猜测地图平面朝哪边
        var tris = new int[seg * 12];
        for (var i = 0; i <= seg; ++i) {
            var a = i * Mathf.PI * 2f / seg;
            var dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
            verts[i * 2] = dir * inner;
            verts[i * 2 + 1] = dir * outer;
        }
        for (var i = 0; i < seg; ++i) {
            var v = i * 2;
            var t = i * 12;
            tris[t] = v; tris[t + 1] = v + 1; tris[t + 2] = v + 2;
            tris[t + 3] = v + 1; tris[t + 4] = v + 3; tris[t + 5] = v + 2;
            tris[t + 6] = v; tris[t + 7] = v + 2; tris[t + 8] = v + 1;
            tris[t + 9] = v + 1; tris[t + 10] = v + 2; tris[t + 11] = v + 3;
        }
        ringMesh = new Mesh { vertices = verts, triangles = tris };
        ringMesh.RecalculateNormals();
        return ringMesh;
    }

    private Material GetMarkerMaterial(bool lowConfidence) {
        if (markerMatNormal == null) {
            markerMatNormal = CreateMarkerMaterial(new Color(1f, 1f, 1f, 1f));
            markerMatLowConf = CreateMarkerMaterial(new Color(1f, 0.85f, 0.1f, 1f));
        }
        return (lowConfidence ? markerMatLowConf : markerMatNormal)!;
    }

    private static Material CreateMarkerMaterial(Color color) {
        var shader = Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Universal Render Pipeline/Lit");
        var mat = new Material(shader);
        mat.color = color;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        return mat;
    }

    // ================= 仿射校准（grid km ↔ 桌面世界坐标） =================

    /// <summary>
    /// 用全部实体的 网格坐标 ↔ 图标世界坐标 做最小二乘拟合（x/y/z 三个分量各一组正规方程）。
    /// 实体图标就贴在地图上，是每条任务现场最可靠的位置参照。
    /// 残差以 km 折算（经 3D 偏导向量长度），超 0.3km 做一轮离群点剔除重拟合；仍超差则 affineReady=false。
    /// </summary>
    private void FitAffine() {
        affineReady = false;
        var fm = FireMission.Instance;
        if (fm == null || fm.Entities == null) return;

        var pts = new List<(Vector2 grid, Vector3 world)>();
        foreach (var kv in fm.Entities) {
            var e = kv.Value;
            if (e?.Location == null) continue;
            pts.Add((new Vector2(e.Position.x, e.Position.y), e.Location.transform.position));
        }
        if (pts.Count < 3) {
            MelonLogger.Warning($"[Intel] 仿射校准: 可用实体不足（{pts.Count}）");
            return;
        }

        // 数据健全性检查（曾出现读出全零/恒等的诡异情况，必须能在日志里看清）
        var allZero = true;
        foreach (var p in pts) {
            if (p.world.sqrMagnitude > 1e-8f) { allZero = false; break; }
        }
        if (allZero) {
            MelonLogger.Error("[Intel] 仿射校准: 全部实体图标的世界坐标读出为零，数据不可用");
            return;
        }

        FitOnce(pts, out aX, out bX, out cX, out aY, out bY, out cY, out aZ, out bZ, out cZ);
        var maxKm = MaxResidualKm(pts, out var meanKm);
        if (maxKm > 0.3f) {
            var good = pts.FindAll(p => ResidualWorld(p) / WorldPerKm() <= 0.3f);
            if (good.Count >= 3 && good.Count < pts.Count) {
                MelonLogger.Msg($"[Intel] 仿射校准: 剔除 {pts.Count - good.Count} 个离群点后重拟合");
                FitOnce(good, out aX, out bX, out cX, out aY, out bY, out cY, out aZ, out bZ, out cZ);
                maxKm = MaxResidualKm(good, out meanKm);
                pts = good;
            }
        }
        affineReady = maxKm <= 0.3f;
        MelonLogger.Msg($"[Intel] 仿射校准: n={pts.Count} world/km={WorldPerKm():F4} " +
                        $"残差 mean={meanKm:F3}km max={maxKm:F3}km → {(affineReady ? "可用" : "不可用")}");

        // 测算 桌面 local 单位/km（给标记环定尺寸）：两个相距 1km 的点过 InverseTransformPoint 比距离
        var surface = GameObject.Find("Draggable Surface")?.transform;
        if (affineReady && surface != null) {
            var probe = new Vector2(10f, 5f);
            var l0 = surface.InverseTransformPoint(AffineForward(probe));
            var l1 = surface.InverseTransformPoint(AffineForward(probe + Vector2.right));
            var d = Vector3.Distance(l0, l1);
            if (d > 1e-6f) markerLocalPerKm = d;

            // 顺带验证：炮塔棋子的实际摆放位置 vs 报文"铁巢"网格经仿射预测的桌面位置
            if (anchors.TryGetValue("铁巢", out var turretGrid)) {
                var turretLocal = fcs?.MapTable.TurretLocalPos();
                if (turretLocal != null) {
                    var pred = surface.InverseTransformPoint(AffineForward(turretGrid));
                    MelonLogger.Msg($"[Intel] 炮塔棋子验证: 报文网格({turretGrid.x:F2},{turretGrid.y:F2}) " +
                                    $"→ 预测local({pred.x:F3},{pred.y:F3}) vs 实际local({turretLocal.Value.x:F3},{turretLocal.Value.y:F3})");
                }
            }
        }
    }

    /// <summary>world/km 比例：两个 3D 偏导向量的平均长度。</summary>
    private float WorldPerKm() {
        var dgx = Mathf.Sqrt(aX * aX + aY * aY + aZ * aZ); // ∂world/∂gx
        var dgy = Mathf.Sqrt(bX * bX + bY * bY + bZ * bZ); // ∂world/∂gy
        var s = (dgx + dgy) * 0.5f;
        return s > 1e-9f ? s : 1f;
    }

    /// <summary>grid(km) → 图标平面上的完整 3D 世界坐标（z 由拟合给出，不能丢）。</summary>
    private Vector3 AffineForward(Vector2 grid) =>
        new(aX * grid.x + bX * grid.y + cX,
            aY * grid.x + bY * grid.y + cY,
            aZ * grid.x + bZ * grid.y + cZ);

    /// <summary>世界坐标 → grid(km)：取 x/z 两个主分量方程解 2x2（y 分量变化太小，不用）。</summary>
    private Vector2 AffineInverse(Vector3 world) {
        var det = aX * bZ - bX * aZ;
        if (Mathf.Abs(det) < 1e-9f) return default;
        var dx = world.x - cX;
        var dz = world.z - cZ;
        return new Vector2((bZ * dx - bX * dz) / det, (-aZ * dx + aX * dz) / det);
    }

    private float ResidualWorld((Vector2 grid, Vector3 world) p) {
        var pred = AffineForward(p.grid);
        return Vector3.Distance(pred, p.world);
    }

    private float MaxResidualKm(List<(Vector2 grid, Vector3 world)> pts, out float meanKm) {
        var wpk = WorldPerKm();
        var max = 0f;
        var sum = 0f;
        foreach (var p in pts) {
            var r = ResidualWorld(p) / wpk;
            sum += r;
            if (r > max) max = r;
        }
        meanKm = pts.Count > 0 ? sum / pts.Count : 0f;
        return max;
    }

    /// <summary>分别对 world.x/y/z 拟合三元线性模型 w = a*gx + b*gy + c（正规方程）。</summary>
    private static void FitOnce(List<(Vector2 grid, Vector3 world)> pts,
                                out float ax, out float bx, out float cx,
                                out float ay, out float by, out float cy,
                                out float az, out float bz, out float cz) {
        double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0, n = pts.Count;
        double sxwx = 0, sywx = 0, swx = 0, sxwy = 0, sywy = 0, swy = 0, sxwz = 0, sywz = 0, swz = 0;
        foreach (var (g, w) in pts) {
            sxx += g.x * g.x; sxy += g.x * g.y; syy += g.y * g.y; sx += g.x; sy += g.y;
            sxwx += g.x * w.x; sywx += g.y * w.x; swx += w.x;
            sxwy += g.x * w.y; sywy += g.y * w.y; swy += w.y;
            sxwz += g.x * w.z; sywz += g.y * w.z; swz += w.z;
        }
        Solve3(sxx, sxy, sx, sxy, syy, sy, sx, sy, n, sxwx, sywx, swx, out ax, out bx, out cx);
        Solve3(sxx, sxy, sx, sxy, syy, sy, sx, sy, n, sxwy, sywy, swy, out ay, out by, out cy);
        Solve3(sxx, sxy, sx, sxy, syy, sy, sx, sy, n, sxwz, sywz, swz, out az, out bz, out cz);
    }

    /// <summary>3x3 高斯消元（部分主元）。</summary>
    private static void Solve3(double a11, double a12, double a13,
                               double a21, double a22, double a23,
                               double a31, double a32, double a33,
                               double b1, double b2, double b3,
                               out float x1, out float x2, out float x3) {
        var m = new double[3, 4] {
            { a11, a12, a13, b1 },
            { a21, a22, a23, b2 },
            { a31, a32, a33, b3 },
        };
        for (var col = 0; col < 3; ++col) {
            var piv = col;
            for (var r = col + 1; r < 3; ++r) {
                if (Math.Abs(m[r, col]) > Math.Abs(m[piv, col])) piv = r;
            }
            for (var k = col; k < 4; ++k) (m[col, k], m[piv, k]) = (m[piv, k], m[col, k]);
            var d = m[col, col];
            if (Math.Abs(d) < 1e-12) d = 1e-12;
            for (var r = 0; r < 3; ++r) {
                if (r == col) continue;
                var f = m[r, col] / d;
                for (var k = col; k < 4; ++k) m[r, k] -= f * m[col, k];
            }
        }
        x1 = (float)(m[0, 3] / m[0, 0]);
        x2 = (float)(m[1, 3] / m[1, 1]);
        x3 = (float)(m[2, 3] / m[2, 2]);
    }

    // ================= 锚点解析 =================

    /// <summary>
    /// 锚点名 → 候选网格坐标列表（通常 1 个；上游主题有多解歧义时多个，沿链传播）。
    /// 顺序：炮位别名 → 唯一友方实体（地面真值，含 #n 编号限定）→ 锚点字典（精确/包含匹配）。
    /// 敌方实体刻意不做锚点：它们的位置本身可能就是待解目标，直接读等于作弊。
    /// </summary>
    private bool TryGetAnchorOptions(string anchorText, out List<Vector2> options) {
        options = new List<Vector2>();
        if (string.IsNullOrWhiteSpace(anchorText)) {
            if (TryGetTurretGrid(out var t)) options.Add(t);
            return options.Count > 0;
        }
        var a = anchorText.Trim();
        foreach (var alias in TurretAliases) {
            if (a.Contains(alias, StringComparison.OrdinalIgnoreCase)) {
                if (TryGetTurretGrid(out var t)) options.Add(t);
                return options.Count > 0;
            }
        }
        if (a.Contains("我")) {
            if (TryGetTurretGrid(out var t)) options.Add(t);
            return options.Count > 0;
        }

        // 唯一匹配的友方实体 = 地面真值：观测员/参考点都是友方单位，实体坐标是精确值，
        // 优于报文的 0.1km 舍入网格与几何解算的两个猜测分支
        var ent = FindUniqueFriendlyEntity(a);
        if (ent != null) {
            var g = WorldToGrid(ent.Position);
            if (verbosePass) MelonLogger.Msg($"[Intel] 锚点 '{a}' → 实体 {ent.ID} @ ({g.x:F2},{g.y:F2})");
            options.Add(g);
            return true;
        }

        if (anchorOptions.TryGetValue(a, out var exact)) {
            options.AddRange(exact);
            return true;
        }
        foreach (var kv in anchorOptions) {
            if (kv.Key.Contains(a, StringComparison.OrdinalIgnoreCase) ||
                a.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)) {
                options.AddRange(kv.Value);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 名字唯一匹配一个友方实体时返回它（锚点地面真值 / 主题多解确认）；多义或无匹配返回 null。
    /// 名字含 "#n" 编号时要求实体 ID 也含相同编号（"观测员#1" → allyspotter#1，排除 #2）。
    /// </summary>
    private static MapEntity? FindUniqueFriendlyEntity(string name) {
        const EntityRoles enemyBits = EntityRoles.Enemy | EntityRoles.Target | EntityRoles.OptionalTarget;
        var fm = FireMission.Instance;
        if (fm == null || fm.Entities == null) return null;
        string? numTag = null;
        var hash = name.IndexOf('#');
        if (hash >= 0 && hash + 1 < name.Length) numTag = name.Substring(hash); // "#1"
        MapEntity? found = null;
        foreach (var kv in fm.Entities) {
            var e = kv.Value;
            if (e == null || (e.Role & enemyBits) != 0) continue;
            if (!EntityMatches(e, name)) continue;
            if (numTag != null && (e.ID == null || !e.ID.Contains(numTag))) continue;
            if (found != null) return null; // 多义匹配，不敢用
            found = e;
        }
        return found;
    }

    /// <summary>判断名字是否指向敌方实体；matched 输出匹配到的实体（无则 null）。</summary>
    private static bool IsEnemyName(string name, out MapEntity? matched) {
        matched = FindEntity(name, onlyFriendly: false);
        if (matched == null) return false;
        const EntityRoles enemyBits = EntityRoles.Enemy | EntityRoles.Target | EntityRoles.OptionalTarget;
        return (matched.Role & enemyBits) != 0;
    }

    private static MapEntity? FindEntity(string name, bool onlyFriendly) {
        const EntityRoles enemyBits = EntityRoles.Enemy | EntityRoles.Target | EntityRoles.OptionalTarget;
        var fm = FireMission.Instance;
        if (fm == null || fm.Entities == null) return null;
        foreach (var kv in fm.Entities) {
            var e = kv.Value;
            if (e == null) continue;
            if (onlyFriendly && (e.Role & enemyBits) != 0) continue;
            if (EntityMatches(e, name)) return e;
        }
        return null;
    }

    private static bool EntityMatches(MapEntity e, string anchor) {
        bool Hit(string? s) =>
            !string.IsNullOrEmpty(s) &&
            (s.Contains(anchor, StringComparison.OrdinalIgnoreCase) ||
             anchor.Contains(s, StringComparison.OrdinalIgnoreCase));
        if (Hit(e.ID) || Hit(e.RawID)) return true;
        if (e.Name != null) {
            if (Hit(e.Name.Key) || Hit(e.Name.Raw)) return true;
            try { if (e.Name.TryGet(out var s) && Hit(s)) return true; }
            catch { /* 本地化查找失败不影响其它匹配 */ }
        }
        return false;
    }

    /// <summary>
    /// 炮位网格坐标。优先级：NestGps 作弊直读（精确）→ 报文/笔记本"铁巢"网格行 →
    /// （可选）炮塔棋子逆仿射换算。紧急转移后（turretRelocationPending），与旧值相同的
    /// 纸带锚点视为过时并拒绝；出现不同的新网格行时自动解除转移状态。
    /// </summary>
    private bool TryGetTurretGrid(out Vector2 gridPos, bool allowPieceFallback = true) {
        // NestGps 作弊：TurretLocationIcon 的世界坐标经逆仿射换算为精确网格位，
        // 跳过整个情报收集过程（读数越界则拒绝采信，防误放）
        if (fcs != null && fcs.Convoy.NestGps && affineReady &&
            fcs.Convoy.TryGetNestGpsWorld(out var nestWorld)) {
            var g = AffineInverse(nestWorld);
            if (IsOnMap(g)) {
                gridPos = g;
                return true;
            }
            if (verbosePass) {
                MelonLogger.Warning($"[Intel] NestGps 读数换算后越界 ({g.x:F2},{g.y:F2})，拒绝采信");
            }
        }

        // 报文/笔记本里的"铁巢"网格行
        foreach (var alias in TurretAliases) {
            if (!anchors.TryGetValue(alias, out gridPos)) continue;
            if (turretRelocationPending) {
                if (Vector2.Distance(gridPos, staleTurretGrid) < 0.01f) {
                    gridPos = default;
                    return false; // 纸带上仍是旧位置，不可信
                }
                turretRelocationPending = false;
                MelonLogger.Msg($"[Intel] 铁巢新位置已确认: ({gridPos.x:F2},{gridPos.y:F2})，转移状态解除");
            }
            return true;
        }

        // 兜底：炮塔棋子桌面位置经逆仿射换算（棋子可能不在真实网格位，仅供参考）
        gridPos = default;
        if (!allowPieceFallback || !affineReady) return false;
        var surface = GameObject.Find("Draggable Surface")?.transform;
        var local = fcs?.MapTable.TurretLocalPos();
        if (surface == null || local == null) return false;
        gridPos = AffineInverse(surface.TransformPoint(local.Value));
        return true;
    }

    /// <summary>
    /// 世界坐标 → 网格坐标。实测：实体 world 的 x/y 就是网格公里坐标（distanceToKmScale=1）。
    /// 注意 FireMission.ToLocalSpace 返回的 x 分量恒为常数（坐标根变换问题），不可用。
    /// </summary>
    private static Vector2 WorldToGrid(Vector3 world) => new(world.x, world.y);

    // ================= 运行时侦察 dump（每次会话仅首次 Survey 执行） =================

    public void DumpDiagnostics() {
        try {
            var fm = FireMission.Instance;
            if (fm == null) {
                MelonLogger.Msg("[Intel.Diag] FireMission.Instance = null（不在战斗场景？）");
            }
            else {
                MelonLogger.Msg($"[Intel.Diag] FireMission: cellW={fm.cellWidth} cellH={fm.cellHeight} " +
                                $"yUp={fm.yIncreasesUp} distToKm={fm.distanceToKmScale}");
                DumpEntities(fm);
            }
        }
        catch (Exception ex) { MelonLogger.Error($"[Intel.Diag] FireMission dump failed: {ex.Message}"); }

        DumpMapSurface();
        DumpSceneTokens();
        DumpIntelTexts();
    }

    private static void DumpEntities(FireMission fm) {
        if (fm.Entities == null) {
            MelonLogger.Msg("[Intel.Diag] Entities = null");
            return;
        }
        MelonLogger.Msg($"[Intel.Diag] ==== Entities ({fm.Entities.Count}) ====");
        var transformDumped = 0;
        foreach (var kv in fm.Entities) {
            var e = kv.Value;
            if (e == null) continue;
            string nameKey = "", nameRaw = "", nameGet = "";
            try {
                if (e.Name != null) {
                    nameKey = e.Name.Key ?? "";
                    nameRaw = e.Name.Raw ?? "";
                    nameGet = e.Name.TryGet(out var s) ? s : "";
                }
            }
            catch { /* 名字读不出不影响主体 */ }

            var loc = e.Location;
            var revealed = loc != null && loc.VisualRoot != null && loc.VisualRoot.activeSelf;
            MelonLogger.Msg(
                $"[Intel.Diag] {e.ID} | raw={e.RawID} | name(k/r/g)={nameKey}/{nameRaw}/{nameGet} | " +
                $"role={e.Role} | state={e.State} | alive={e.IsAlive} | hp={e.Health}/{e.MaxHealth} | " +
                $"world=({e.Position.x:F2},{e.Position.y:F2},{e.Position.z:F2}) | " +
                $"revealed={revealed} | locObj={(loc != null ? loc.name : "null")}");

            // 前 8 个实体附带图标的运行时变换（仿射校准的数据源验证）
            if (loc != null && transformDumped < 8) {
                transformDumped++;
                var wp = loc.transform.position;
                MelonLogger.Msg(
                    $"[Intel.Diag]     icon world=({wp.x:F4},{wp.y:F4},{wp.z:F4}) path={GetPath(loc.transform)}");
            }
        }
    }

    /// <summary>地图桌 Draggable Surface 的全部子物体：名字、组件类型、局部坐标。棋子机制侦察用。</summary>
    private static void DumpMapSurface() {
        try {
            var surface = GameObject.Find("Draggable Surface")?.transform;
            if (surface == null) {
                MelonLogger.Msg("[Intel.Diag] Draggable Surface 未找到");
                return;
            }
            MelonLogger.Msg($"[Intel.Diag] ==== Draggable Surface children ({surface.childCount}) ====");
            for (var i = 0; i < surface.childCount; ++i) {
                var t = surface.GetChild(i);
                DumpTransformShallow(t, "  ");
                // TurretGrid 可能是地图网格标签的容器，展开一层看有没有 "A2" 之类的坐标物体
                if (t.name == "TurretGrid") {
                    for (var j = 0; j < t.childCount && j < 30; ++j) {
                        DumpTransformShallow(t.GetChild(j), "    ");
                    }
                }
            }
        }
        catch (Exception ex) { MelonLogger.Error($"[Intel.Diag] surface dump failed: {ex.Message}"); }
    }

    private static void DumpTransformShallow(Transform t, string indent) {
        var comps = new System.Text.StringBuilder();
        foreach (var c in t.GetComponents<Component>()) {
            if (c == null) continue;
            if (comps.Length > 0) comps.Append(',');
            comps.Append(c.GetType().Name);
        }
        var tmp = t.GetComponentInChildren<TMP_Text>();
        var lp = t.localPosition;
        MelonLogger.Msg(
            $"[Intel.Diag] {indent}'{t.name}' active={t.gameObject.activeSelf} " +
            $"local=({lp.x:F4},{lp.y:F4},{lp.z:F4}) text='{(tmp != null ? tmp.text : "")}' comps=[{comps}]");
    }

    /// <summary>全场景搜名字含 Token 的物体（找棋子托盘/备件）。重操作，仅侦察时调用。</summary>
    private static void DumpSceneTokens() {
        try {
            var all = Object.FindObjectsOfType<Transform>();
            var count = 0;
            foreach (var t in all) {
                if (t == null || t.name.IndexOf("token", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (++count > 40) {
                    MelonLogger.Msg("[Intel.Diag] ... token 物体过多，截断");
                    break;
                }
                MelonLogger.Msg($"[Intel.Diag] sceneToken '{GetPath(t)}' active={t.gameObject.activeSelf}");
            }
            MelonLogger.Msg($"[Intel.Diag] ==== scene tokens: {count} ====");
        }
        catch (Exception ex) { MelonLogger.Error($"[Intel.Diag] scene token dump failed: {ex.Message}"); }
    }

    private static string GetPath(Transform t) {
        var path = t.name;
        while (t.parent != null) {
            t = t.parent;
            path = t.name + "/" + path;
        }
        return path;
    }

    private void DumpIntelTexts() {
        try {
            var sections = Object.FindObjectsOfType<NotepadSection>();
            MelonLogger.Msg($"[Intel.Diag] ==== NotepadSections ({sections.Count}) ====");
            foreach (var section in sections) {
                if (section == null) continue;
                var txt = section.TargetText != null ? section.TargetText.text ?? "" : "";
                MelonLogger.Msg($"[Intel.Diag] --- section '{section.UnityTag}' ({txt.Length} chars) ---");
                foreach (var line in txt.Split('\n')) {
                    if (line.Trim().Length > 0) MelonLogger.Msg($"[Intel.Diag]   {line.TrimEnd()}");
                }
            }
        }
        catch (Exception ex) { MelonLogger.Error($"[Intel.Diag] notepad dump failed: {ex.Message}"); }

        foreach (Teleprinter.Teleprinters type in Enum.GetValues(typeof(Teleprinter.Teleprinters))) {
            try {
                var tp = Teleprinter.GetTeleprinter(type);
                if (tp == null) {
                    MelonLogger.Msg($"[Intel.Diag] Teleprinter {type}: null");
                    continue;
                }
                if (tp.paperTransform == null) {
                    MelonLogger.Msg($"[Intel.Diag] Teleprinter {type}: paperTransform = null");
                    continue;
                }
                var pt = tp.paperTransform;
                MelonLogger.Msg($"[Intel.Diag] --- Teleprinter {type} paper '{pt.name}' children={pt.childCount} ---");
                var selfTmp = pt.GetComponent<TMP_Text>() ?? pt.GetComponentInChildren<TMP_Text>();
                if (selfTmp != null) {
                    MelonLogger.Msg($"[Intel.Diag]   纸带TMP（{selfTmp.name}）全文:");
                    foreach (var line in (selfTmp.text ?? "").Split('\n')) {
                        if (line.Trim().Length > 0) MelonLogger.Msg($"[Intel.Diag]     {line.TrimEnd()}");
                    }
                }
                else {
                    MelonLogger.Msg("[Intel.Diag]   纸带上未找到 TMP_Text");
                }
            }
            catch (Exception ex) { MelonLogger.Error($"[Intel.Diag] teleprinter {type} dump failed: {ex.Message}"); }
        }
    }

    // ================= IMGUI 状态行 =================

    /// <summary>
    /// 窗口行格式：`>#3 H10 9:7 !  敌方炮兵指挥…`
    /// 关键信息前置：序号（与地图标签对应）、网格区号、低置信度 ! 标记；名字截断放最后。
    /// </summary>
    private void RebuildWindowLines() {
        WindowLines.Clear();
        var placed = CountPlaced();
        var ignored = CountIgnored();
        if (Candidates.Count == 0) {
            WindowLines.Add(placed + ignored > 0
                ? $"Targets: 0 active ({placed} placed, {ignored} hidden)"
                : "Survey: no candidates");
            return;
        }
        WindowLines.Add($"Targets: {Candidates.Count} active, {placed} placed, {ignored} hidden");
        for (var i = 0; i < Mathf.Min(Candidates.Count, 6); ++i) {
            var c = Candidates[i];
            var e = activeEntries[i];
            var mark = i == CurrentIndex ? ">" : " ";
            var zone = FcsWindow.ConvertPosition(new Vector3(c.Point.x, c.Point.y, 0f));
            var conf = c.LowConfidence ? " !" : "";
            WindowLines.Add($"{mark}#{e.Seq} {zone}{conf} {Truncate(c.Name, 10)}");
        }
        if (Candidates.Count > 6) {
            WindowLines.Add($"  ... +{Candidates.Count - 6} more");
        }
    }

    private static string Truncate(string s, int maxChars) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= maxChars ? s : s.Substring(0, maxChars) + "…";
}
