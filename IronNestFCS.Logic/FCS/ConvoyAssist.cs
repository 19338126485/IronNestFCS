using System.Collections;
using Il2Cpp;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 紧急转移 / 后勤车队辅助系统：
///  - 转移检测：轮询 TurretLocationIcon（地图图标）位置，突变即判定铁巢已转移，
///    通知 <see cref="IntelSystem"/> 作废旧炮位锚点。
///    （实测：TurretController.transform.position 恒定不变，不能用作检测或直读；
///    唯一跟踪真实炮位的对象是 TurretLocationIcon。）
///  - NestGps（作弊开关，默认关）：直接读 TurretLocationIcon 世界坐标，
///    由 IntelSystem 逆仿射换算成精确网格位（越界拒绝），跳过情报收集过程。
///  - AutoConvoy（默认开）：转移后铁巢未定位且大格公告已出现时，自动打"位置报告"卡
///    （id=LocationReport，控制台参数为 Coordinate 变量：L=列字母 N=行号）征集车队情报，
///    直到 IntelSystem 解出唯一新炮位（或尝试次数/卡牌耗尽）。
///  - <see cref="DumpCardRecon"/>：卡牌 / 征用插槽 / 控制台变量结构侦察 dump。
///
/// 重载安全：不注册 IL2CPP 类型；不持静态 IL2CPP 引用；协程经 FSC.RunTracked 登记；
/// ShutDown 清空全部引用。
/// </summary>
public class ConvoyAssist {
    /// <summary>自动后勤车队征集开关（默认开）。</summary>
    public bool AutoConvoy { get; private set; } = true;

    /// <summary>作弊：直读炮位图标精确位置（默认关）。</summary>
    public bool NestGps { get; private set; }

    private FSC? fcs;
    private IntelSystem? intel;

    private float nextPollTime;
    private bool hasBaseline;
    private Vector3 lastIconPos;
    private bool inTransit;      // 转移移动是持续数十秒的滑动：整个episode只通报一次
    private float lastMoveTime;
    private TurretLocationIcon? cachedIcon;
    private IntPtr lastMissionPtr;

    // 自动车队循环
    private bool convoyRunning;
    private string? exhaustedCell; // 该大格的尝试次数已耗尽：同一 cell 不再自动重试（防反复烧征用点）
    private const int MaxConvoyAttempts = 4;
    /// <summary>卡牌入槽的物理目标点（与 PurchaseDeck 买弹药共用同一征用槽位坐标）。</summary>
    private static readonly Vector3 SlotDropPos = new(6.4814f, -2.4675f, -22.0968f);

    public void Bind(FSC fcs, IntelSystem intel) {
        this.fcs = fcs;
        this.intel = intel;
    }

    public void ShutDown() {
        fcs = null;
        intel = null;
        cachedIcon = null;
        hasBaseline = false;
        convoyRunning = false;
        lastMissionPtr = IntPtr.Zero;
    }

    public bool ToggleAutoConvoy() {
        AutoConvoy = !AutoConvoy;
        MelonLogger.Msg($"[Convoy] 自动后勤车队征集: {(AutoConvoy ? "开" : "关")}");
        return AutoConvoy;
    }

    public bool ToggleNestGps() {
        NestGps = !NestGps;
        MelonLogger.Msg($"[Convoy] 炮位直读（作弊）: {(NestGps ? "开" : "关")}");
        if (NestGps) {
            // 打开立即生效一次：重放炮塔棋子（走 Survey 的锚点/校准管道）
            try { intel?.Survey(); }
            catch (Exception ex) { MelonLogger.Error($"[Convoy] NestGps 立即生效失败: {ex.Message}"); }
        }
        return NestGps;
    }

    // ================= 转移检测（每秒轮询） =================

    private const float PollIntervalSeconds = 1f;
    /// <summary>图标位移判定阈值（世界单位，world/km≈0.2122，0.1 ≈ 0.5km）。转移必然移动数 km。</summary>
    private const float MovedThreshold = 0.1f;

    public void Tick() {
        if (fcs == null || intel == null) return;
        if (Time.realtimeSinceStartup < nextPollTime) return;
        nextPollTime = Time.realtimeSinceStartup + PollIntervalSeconds;

        // 换任务：重置基线，不把上一场的位置当"旧位置"
        var fm = FireMission.Instance;
        var ptr = fm == null ? IntPtr.Zero : fm.Pointer;
        if (ptr != lastMissionPtr) {
            lastMissionPtr = ptr;
            hasBaseline = false;
            cachedIcon = null;
            convoyRunning = false;
            exhaustedCell = null;
            inTransit = false;
        }
        if (fm == null) return;

        var icon = FindTurretIcon();
        if (icon == null) return;
        Vector3 iconPos;
        try { iconPos = icon.transform.position; }
        catch { cachedIcon = null; return; }

        if (!hasBaseline) {
            hasBaseline = true;
            lastIconPos = iconPos;
            MelonLogger.Msg($"[Convoy] 炮位基线: 图标={Fmt(iconPos)}");
            return;
        }

        if (Vector3.Distance(iconPos, lastIconPos) > MovedThreshold) {
            lastIconPos = iconPos;
            lastMoveTime = Time.realtimeSinceStartup;
            if (!inTransit) {
                inTransit = true;
                MelonLogger.Msg($"[Convoy] 检测到铁巢转移（滑动开始）: 图标自 {Fmt(iconPos)} 起移动");
                intel.OnTurretRelocated();
            }
            return; // 滑动途中不重复通报（实测转移是持续数十秒的连续滑动）
        }
        if (inTransit && Time.realtimeSinceStartup - lastMoveTime > 3f) {
            inTransit = false;
            MelonLogger.Msg($"[Convoy] 转移滑动结束: 图标停在 {Fmt(iconPos)}");
        }
    }

    private TurretLocationIcon? FindTurretIcon() {
        if (cachedIcon != null) return cachedIcon;
        var icons = Object.FindObjectsOfType<TurretLocationIcon>();
        cachedIcon = icons.Length > 0 ? icons[0] : null;
        return cachedIcon;
    }

    private static string Fmt(Vector3 p) => $"({p.x:F2},{p.y:F2},{p.z:F2})";

    // ================= NestGps 作弊直读 =================

    /// <summary>
    /// 作弊：读炮位图标的世界坐标（换算/越界校验在 IntelSystem 侧做，那里有仿射校准）。
    /// </summary>
    public bool TryGetNestGpsWorld(out Vector3 world) {
        world = default;
        TurretLocationIcon? icon = null;
        try { icon = FindTurretIcon(); }
        catch { return false; }
        if (icon == null) return false;
        try { world = icon.transform.position; }
        catch { cachedIcon = null; return false; }
        return true;
    }

    // ================= 自动后勤车队（位置报告卡） =================

    /// <summary>IntelSystem 通报新一轮转移：重置"失败不再重试"记录（新一轮允许重新打卡）。</summary>
    public void OnRelocationEpoch() {
        exhaustedCell = null;
    }

    /// <summary>IntelSystem 在转移未定位且大格已知时调用。已在跑则忽略（重入安全）。</summary>
    public void RequestConvoyIntel(string cell) {
        if (fcs == null || intel == null || convoyRunning) return;
        if (cell == exhaustedCell) return; // 这个大格已经试过且失败了，不再自动烧征用点
        convoyRunning = true;
        fcs.RunTracked(ConvoyRoutine(cell));
    }

    private IEnumerator ConvoyRoutine(string cell) {
        try {
            var letter = cell.Substring(0, 1);
            if (!int.TryParse(cell.Substring(1), out var row)) {
                MelonLogger.Error($"[Convoy] 大格编号无法解析: '{cell}'");
                yield break;
            }
            MelonLogger.Msg($"[Convoy] 开始自动车队征集: 参数 {cell}");
            for (var attempt = 1; attempt <= MaxConvoyAttempts; ++attempt) {
                if (intel == null || fcs == null) yield break;
                if (!intel.TurretRelocationPending) {
                    MelonLogger.Msg("[Convoy] 铁巢新位置已解出，车队征集结束");
                    yield break;
                }
                if (NestGps) yield break; // 作弊直读开着就不需要车队了

                // 找卡与槽
                var card = FindLocationReportCard();
                var slot = FindRequisitionSlot();
                if (card == null || slot == null) {
                    MelonLogger.Warning($"[Convoy] 未找到位置报告卡或征用槽（card={(card != null)} slot={(slot != null)}），停止");
                    yield break;
                }

                // 打卡全程持桌面锁：与买弹药/任务流程互斥（同一台征用设备）
                yield return fcs.AcquireDeskLock();
                try {
                    // 槽里躺着别的卡（实测：玩家打过的紧急转移卡会留在槽里）→ 用槽自己的 API 弹出；
                    // 躺着的就是位置报告卡（上次尝试残留）→ 直接复用，跳过物理入槽
                    var alreadyInSlot = slot.CurrentCard == card;
                    if (slot.HasCard && !alreadyInSlot) {
                        MelonLogger.Msg($"[Convoy] 征用槽被 {slot.CurrentCard?.CurrentDefinition?.ID ?? "?"} 占用，先弹出");
                        try { slot.RemoveCard(slot.CurrentCard, true); }
                        catch (Exception ex) { MelonLogger.Error($"[Convoy] 弹出占槽卡牌失败: {ex.Message}"); }
                        yield return new WaitForSeconds(0.5f);
                        if (slot.HasCard) {
                            MelonLogger.Warning("[Convoy] 占槽卡牌弹出失败，本次放弃");
                            yield break;
                        }
                    }

                    if (!alreadyInSlot) {
                        MelonLogger.Msg($"[Convoy] 第 {attempt}/{MaxConvoyAttempts} 次打卡: {card.CurrentDefinition?.ID} → {cell}");
                        card.transform.position = SlotDropPos;
                        var drag = card.GetComponent<DraggableItem>();
                        if (drag != null) drag.MoveToSlot();
                        yield return new WaitForSeconds(0.8f);
                        if (slot.CurrentCard == null) {
                            MelonLogger.Warning("[Convoy] 卡牌未入槽，本次打卡失败");
                            continue;
                        }
                    }
                    else {
                        MelonLogger.Msg($"[Convoy] 第 {attempt}/{MaxConvoyAttempts} 次打卡: 复用槽内位置报告卡 → {cell}");
                    }
                    // 设大格参数：控制台里的 Coordinate 变量（L=列字母, N=行号）
                    var console = slot.CurrentCardConsole;
                    var vars = console == null ? null : console.GetComponentsInChildren<PunchcardVariable>();
                    var coordSet = false;
                    if (vars != null) {
                        foreach (var v in vars) {
                            if (v == null || v.VariableType != PunchcardVariable.VariableTypes.Coordinate) continue;
                            v.SetCoordinate_GridLocation_L(letter);
                            v.SetCoordinate_GridLocation_N(row);
                            coordSet = true;
                        }
                    }
                    if (!coordSet) {
                        MelonLogger.Warning("[Convoy] 未找到 Coordinate 参数变量，按控制台默认值打卡");
                    }
                    yield return new WaitForSeconds(0.2f);
                    slot.AttemptRequisition();
                }
                finally {
                    fcs.ReleaseDeskLock();
                }

                // 等电报机打印 + 情报哈希轮询的自动重解析（3s 周期），余量给足
                yield return new WaitForSeconds(6f);
                try { intel.Survey(); } // 立即重解析评估：解出则下轮循环自行退出
                catch (Exception ex) { MelonLogger.Error($"[Convoy] 打卡后重解析失败: {ex.Message}"); }
            }
            if (intel != null && intel.TurretRelocationPending) {
                exhaustedCell = cell;
                MelonLogger.Warning($"[Convoy] {MaxConvoyAttempts} 次打卡后铁巢仍未定位，停止且不再自动重试 {cell}" +
                                    "（征用点/卡牌耗尽或报文格式未覆盖）");
            }
        }
        finally {
            convoyRunning = false;
        }
    }

    private static PunchcardRuntime? FindLocationReportCard() {
        foreach (var card in Object.FindObjectsOfType<PunchcardRuntime>()) {
            if (card == null) continue;
            try {
                var def = card.CurrentDefinition;
                if (def == null) continue;
                if (def.ID == "LocationReport") return card;
                var title = card.nameText != null ? card.nameText.text : "";
                if (!string.IsNullOrEmpty(title) &&
                    (title.Contains("位置报告") || title.Contains("后勤") || title.Contains("车队"))) return card;
            }
            catch { /* 单卡读取失败跳过 */ }
        }
        return null;
    }

    private static RequisitionSlot? FindRequisitionSlot() {
        var slots = Object.FindObjectsOfType<RequisitionSlot>();
        return slots.Length > 0 ? slots[0] : null;
    }

    // ================= 卡牌侦察 dump =================

    /// <summary>
    /// 卡牌系统侦察：全部 PunchcardRuntime（ID/标题/费用/次数）、征用插槽状态、
    /// 插槽内卡牌的控制台变量结构（后勤车队的参数入口）、炮位相关对象坐标。
    /// 每次手动 Survey 都执行——卡牌状态随对局变化，侦察需要覆盖打牌前后的快照。
    /// </summary>
    public void DumpCardRecon() {
        try {
            MelonLogger.Msg("[Convoy.Diag] ==== 卡牌侦察 ====");
            DumpTurretObjects();
            DumpAllCards();
            DumpRequisitionSlots();
        }
        catch (Exception ex) {
            MelonLogger.Error($"[Convoy.Diag] dump failed: {ex.Message}");
        }
    }

    private void DumpTurretObjects() {
        try {
            var tc = TurretController.Instance;
            MelonLogger.Msg($"[Convoy.Diag] TurretController: {(tc == null ? "null" : "pos=" + Fmt(tc.transform.position))}");
        }
        catch (Exception ex) { MelonLogger.Msg($"[Convoy.Diag] TurretController 读取失败: {ex.Message}"); }
        try {
            var icons = Object.FindObjectsOfType<TurretLocationIcon>();
            foreach (var icon in icons) {
                if (icon == null) continue;
                var vr = icon.VisualRoot;
                MelonLogger.Msg($"[Convoy.Diag] TurretLocationIcon '{icon.name}': pos={Fmt(icon.transform.position)} " +
                                $"visual={(vr == null ? "null" : vr.activeSelf.ToString())}");
            }
        }
        catch (Exception ex) { MelonLogger.Msg($"[Convoy.Diag] TurretLocationIcon 读取失败: {ex.Message}"); }
    }

    private static void DumpAllCards() {
        var cards = Object.FindObjectsOfType<PunchcardRuntime>();
        MelonLogger.Msg($"[Convoy.Diag] ==== PunchcardRuntime ({cards.Length}) ====");
        foreach (var card in cards) {
            if (card == null) continue;
            try {
                var def = card.CurrentDefinition;
                var title = card.nameText != null ? card.nameText.text : "";
                MelonLogger.Msg($"[Convoy.Diag] card '{card.name}' id={(def == null ? "?" : def.ID)} " +
                                $"title='{title}' cost={(def == null ? -1 : def.Cost)} " +
                                $"uses={(def == null ? -1 : def.RemainingUses)}/{(def == null ? -1 : def.MaxUses)} " +
                                $"active={card.gameObject.activeSelf} autoEject={(def != null && def.AutoEject)}");
            }
            catch (Exception ex) { MelonLogger.Msg($"[Convoy.Diag] card dump 失败: {ex.Message}"); }
        }
    }

    private static void DumpRequisitionSlots() {
        var slots = Object.FindObjectsOfType<RequisitionSlot>();
        MelonLogger.Msg($"[Convoy.Diag] ==== RequisitionSlot ({slots.Length}) ====");
        foreach (var slot in slots) {
            if (slot == null) continue;
            try {
                var cur = slot.CurrentCard;
                MelonLogger.Msg($"[Convoy.Diag] slot '{slot.name}': hasCard={slot.HasCard} " +
                                $"card={(cur == null ? "-" : cur.CurrentDefinition?.ID ?? "?")}");
                var console = slot.CurrentCardConsole;
                if (console == null) continue;
                MelonLogger.Msg($"[Convoy.Diag]   console '{console.name}':");
                DumpTransformRecursive(console.transform, "    ", 0);
            }
            catch (Exception ex) { MelonLogger.Msg($"[Convoy.Diag] slot dump 失败: {ex.Message}"); }
        }
    }

    private static void DumpTransformRecursive(Transform t, string indent, int depth) {
        if (depth > 3) return;
        for (var i = 0; i < t.childCount; ++i) {
            var c = t.GetChild(i);
            var desc = new System.Text.StringBuilder();
            var pv = c.GetComponent<PunchcardVariable>();
            if (pv != null) {
                desc.Append($" PunchcardVariable(id='{pv.VariableID}' type={pv.VariableType}" +
                            $" int={pv.VariableInt} float={pv.VariableFloat} text='{pv.VariableText}'");
                try {
                    if (pv.VariableType == PunchcardVariable.VariableTypes.Coordinate && pv.VariableCoordinate != null)
                        desc.Append($" coord={pv.VariableCoordinate}");
                }
                catch { /* 坐标未初始化时读取可能失败 */ }
                desc.Append(')');
            }
            if (c.GetComponent<DialInteractable>() != null) desc.Append(" DialInteractable");
            if (c.GetComponent<LookAtTarget>() != null) desc.Append(" LookAtTarget");
            MelonLogger.Msg($"[Convoy.Diag] {indent}'{c.name}'{desc}");
            DumpTransformRecursive(c, indent + "  ", depth + 1);
        }
    }
}
