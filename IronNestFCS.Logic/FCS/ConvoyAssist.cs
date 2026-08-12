using Il2Cpp;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 紧急转移 / 后勤车队辅助系统：
///  - 转移检测：轮询 TurretController（物理炮位）与 TurretLocationIcon（地图图标）的位置，
///    任一突变即判定铁巢已转移，通知 <see cref="IntelSystem"/> 作废旧炮位锚点
///    （纸带上出现新的"铁巢"网格行时自动恢复，见 IntelSystem.TryGetTurretGrid）。
///  - NestGps（作弊开关，默认关）：直接读 TurretController 的精确位置作炮位真值，
///    跳过后勤车队的信息收集——绕过卡牌经济，与 Reveal 同性质，故默认关闭。
///  - AutoConvoy（默认开）：转移后情报不足以精确定位铁巢时，自动以"后勤车队"卡牌
///    按大格参数征集情报。【打牌与报文解析待格式侦察（DumpCardRecon）完成后接线】
///  - <see cref="DumpCardRecon"/>：卡牌 / 征用插槽 / 控制台变量结构侦察 dump。
///
/// 重载安全：不注册 IL2CPP 类型；不持静态 IL2CPP 引用；ShutDown 清空。
/// </summary>
public class ConvoyAssist {
    /// <summary>自动后勤车队征集开关（默认开）。</summary>
    public bool AutoConvoy { get; private set; } = true;

    /// <summary>作弊：直读炮塔精确位置（默认关）。</summary>
    public bool NestGps { get; private set; }

    private FSC? fcs;
    private IntelSystem? intel;

    private float nextPollTime;
    private bool hasBaseline;
    private Vector3 lastTurretPos;
    private Vector3 lastIconPos;
    private TurretLocationIcon? cachedIcon;
    private IntPtr lastMissionPtr;

    public void Bind(FSC fcs, IntelSystem intel) {
        this.fcs = fcs;
        this.intel = intel;
    }

    public void ShutDown() {
        fcs = null;
        intel = null;
        cachedIcon = null;
        hasBaseline = false;
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
    /// <summary>位置突变判定阈值。炮塔位置疑为网格公里尺度（侦察确认中），转移必然移动数 km。</summary>
    private const float MovedThreshold = 0.5f;

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
        }
        if (fm == null) return;

        Vector3 turretPos = default;
        var haveTurret = false;
        try {
            var tc = TurretController.Instance;
            if (tc != null) {
                turretPos = tc.transform.position;
                haveTurret = true;
            }
        }
        catch { /* 场景切换瞬间 Instance 可能悬空，下秒再试 */ }

        var iconPos = default(Vector3);
        var haveIcon = false;
        try {
            if (cachedIcon == null) {
                var icons = Object.FindObjectsOfType<TurretLocationIcon>();
                cachedIcon = icons.Length > 0 ? icons[0] : null;
            }
            if (cachedIcon != null) {
                iconPos = cachedIcon.transform.position;
                haveIcon = true;
            }
        }
        catch { cachedIcon = null; }

        if (!haveTurret && !haveIcon) return;
        if (!hasBaseline) {
            hasBaseline = true;
            lastTurretPos = turretPos;
            lastIconPos = iconPos;
            MelonLogger.Msg($"[Convoy] 炮位基线: TurretController={(haveTurret ? Fmt(turretPos) : "null")} " +
                            $"Icon={(haveIcon ? Fmt(iconPos) : "null")}");
            return;
        }

        var turretMoved = haveTurret && Vector3.Distance(turretPos, lastTurretPos) > MovedThreshold;
        var iconMoved = haveIcon && Vector3.Distance(iconPos, lastIconPos) > MovedThreshold;
        if (!turretMoved && !iconMoved) return;

        MelonLogger.Msg($"[Convoy] 检测到铁巢转移: " +
                        $"{(turretMoved ? $"TurretController {Fmt(lastTurretPos)}→{Fmt(turretPos)} " : "")}" +
                        $"{(iconMoved ? $"Icon {Fmt(lastIconPos)}→{Fmt(iconPos)}" : "")}");
        lastTurretPos = turretPos;
        lastIconPos = iconPos;
        intel.OnTurretRelocated();

        if (AutoConvoy && !NestGps) {
            // TODO(侦察后接线)：自动找后勤车队卡 → PlaceCard → 设大格参数 → AttemptRequisition。
            // 大格参数可由 turretPos 向下取整得到（游戏本就免费告知大格，不算作弊）。
            MelonLogger.Msg("[Convoy] AutoConvoy 已开：自动车队征集将在报文格式侦察完成后启用（当前请先手动打车队卡）");
        }
    }

    private static string Fmt(Vector3 p) => $"({p.x:F2},{p.y:F2},{p.z:F2})";

    // ================= NestGps 作弊直读 =================

    /// <summary>
    /// 作弊：直接读 TurretController 的精确位置作为炮位网格坐标。
    /// 仅当读数落在网格公里尺度（0..20 / 0..10）内才采信；范围外说明坐标空间猜错，拒绝并打日志。
    /// </summary>
    public bool TryGetNestGpsGrid(out Vector2 grid) {
        grid = default;
        TurretController? tc = null;
        try { tc = TurretController.Instance; }
        catch { return false; }
        if (tc == null) return false;
        var p = tc.transform.position;
        if (p.x is >= -1f and <= 21f && p.y is >= -1f and <= 11f) {
            grid = new Vector2(p.x, p.y);
            return true;
        }
        return false;
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
