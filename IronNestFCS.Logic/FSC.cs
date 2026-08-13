using HarmonyInstance = HarmonyLib.Harmony;
using System.Collections;
using Il2Cpp;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace IronNestFCS.Logic;

public enum LeftRight {
    Left,
    Right,
}

/// <summary>
/// 纯火控领域逻辑：查找游戏对象、读取游戏数据、操控游戏内交互（dial 等）。
/// 不含任何 UI / IMGUI / 生命周期框架代码——那些在 <see cref="FcsModule"/> 和 <see cref="FcsWindow"/> 里。
///
/// 重载安全规则：
///  - 不要在这里注册新的 IL2CPP 类型（同一类型进程内只能注册一次）。
///  - 每次实例用独立的 Harmony 实例；Shutdown 时 UnpatchSelf。
///  - 所有对 IL2CPP 对象的引用在 Shutdown 时清空，便于旧 ALC 回收。
/// </summary>
public class FSC
{
    private const string HarmonyId = "com.svr2kos2.ironnestfcs.logic";

    // ===== 药包自动补充 =====
    // 两炮共用一个装药余量池：余量低于 PowderReplenishThreshold 时，每 PowderCheckInterval 秒
    // 自动购买一次装药卡，把药包维持在充足水位，避免任务流程因装药不足卡在装填/击发阶段。
    // 只做检测与补充，不干预 RunTaskRoutine 里的任何现有步骤。
    private const float PowderCheckInterval = 5f;
    private const int PowderReplenishThreshold = 6;

    private HarmonyInstance? _harmony;
    
    private FcsSceneInteractor _sceneInteractor;
    private readonly PurchaseDeck _purchaseDeck = new();
    public readonly MapTable MapTable = new MapTable();
    public readonly BallisticCalculator BallisticCalculator = new BallisticCalculator();
    public readonly GunSystem LeftGun = new GunSystem();
    public readonly GunSystem RightGun = new GunSystem();
    public readonly Turret Turret = new Turret();
    public readonly TriggerConsole TriggerConsole = new();
    /// <summary>情报/测绘子系统（自动解析情报、一键揭示全图）。</summary>
    public readonly IntelSystem Intel = new();
    /// <summary>紧急转移/后勤车队辅助（转移检测、NestGps 作弊直读、卡牌侦察）。</summary>
    public readonly ConvoyAssist Convoy = new();
    /// <summary>移动目标（列车/舰船）：行进路线推算与定时开火。</summary>
    public readonly MovingTargetSystem Moving = new();
    
    // ===== 任务调度 =====
    // 用户不再指定炮管：任务入队后由调度器派给空闲炮管，炮管打完一发自动拉下一个。
    // 所有读写都在 Unity 主线程（入队来自点击回调，派发/完成来自协程），无并发，无需锁。
    private readonly Queue<ArtilleryTask> _taskQueue = new();

    /// <summary>当前各炮管正在执行的任务；null 表示该炮管空闲。供 UI 显示与调度判断。</summary>
    public ArtilleryTask? LeftTask { get; private set; }
    public ArtilleryTask? RightTask { get; private set; }

    /// <summary>等待派发的任务数（已入队但还没分到炮管）。供 UI 显示。</summary>
    public int PendingCount => _taskQueue.Count;
    public Queue<ArtilleryTask> QueueCan => new Queue<ArtilleryTask>(_taskQueue);

    /// <summary>
    /// 控制台互斥锁：保护弹道计算器、确认开关台、采购台这三组全局唯一的"短操作"硬件。
    /// 临界区都很短（解算 / 确认弹 / 击发前的确认+击发），用完即放。
    /// </summary>
    private readonly CoroutineLock _deskLock = new();

    /// <summary>
    /// 炮塔方向角锁：方向角是全炮塔共享的，且一旦为某任务转到位，必须独占到这一发打出去为止
    /// （中途被另一任务转走就会打偏）。与 <see cref="_deskLock"/> 分开，是为了让本任务能在
    /// 后台早早抢占炮塔、与装填/升仰角重叠，而不挡住另一管炮在 deskLock 上的解算。
    ///
    /// 防死锁：凡同时需要两把锁处，一律"先 turret 后 desk"。本类只有击发段会嵌套两把锁
    /// （此时炮塔已由后台预约持有，再去抢 desk），解算/确认弹只单独用 desk，故无环、不死锁。
    /// </summary>
    private readonly CoroutineLock _turretLock = new();

    // 正在运行的协程句柄。Dispose 时全部停掉，避免热重载后旧 ALC 的协程继续执行导致崩溃。
    private readonly List<object> _runningCoroutines = new();
    public FSC() {
        this._sceneInteractor = new FcsSceneInteractor(this);
    }

    public bool IsBound { get; private set; } = false;

    /// <summary>查找并绑定游戏对象。返回 false 表示当前场景还没有目标控件。</summary>
    public bool TryBind()
    {
        // 每次重载创建全新的 Harmony 实例，避免与上一版补丁冲突。
        _sceneInteractor = new FcsSceneInteractor(this);
        _sceneInteractor.Initialize();
        _harmony = new HarmonyInstance(HarmonyId);
        _deskLock.Reset();
        _turretLock.Reset();
        // 情报系统不依赖场景控件绑定：即使炮塔/控制台没绑上，Survey 的侦察 dump 仍然有用。
        Intel.Bind(this);
        Convoy.Bind(this, Intel);
        Moving.Bind(this, Intel);
        IsBound = MapTable.TryBind()
                  && BallisticCalculator.TryBind()
                  && LeftGun.TryBind("Left")
                  && RightGun.TryBind("Right")
                  && _purchaseDeck.TryBind()
                  && Turret.TryBind()
                  && TriggerConsole.TryBind();
        MelonLogger.Msg("[FCS] Initialize: " + (IsBound ? "success" : "failed"));
        if (IsBound) {
            // 常驻药包自动补充协程：仅保证装药余量充足，不改动任务流程。
            _runningCoroutines.Add(MelonCoroutines.Start(ReplenishPowderLoop()));
        }
        // _runningCoroutines.Add(MelonCoroutines.Start(ExposeAllEntities()));
        
        return IsBound;
    }

    public void Update() {
        _sceneInteractor.Update();
        Intel.Tick();
        Convoy.Tick();
    }

    /// <summary>场景交互器（IntelSystem 创建标记/标签时用其 AddText 与销毁登记）。</summary>
    public FcsSceneInteractor Interactor => _sceneInteractor;
    
    /// <summary>释放：撤销补丁、清空 IL2CPP 引用。</summary>
    public void Dispose()
    {
        // 停掉所有未完成的协程，否则热重载后旧 ALC 的协程仍会被 Unity 驱动 → 崩溃。
        foreach (var handle in _runningCoroutines) {
            try { MelonCoroutines.Stop(handle); }
            catch (Exception ex) { MelonLogger.Error($"[FCS] Stop coroutines failed: {ex}"); }
        }
        _runningCoroutines.Clear();

        // 清空调度状态，避免热重载后残留任务/槽位影响新一轮绑定。
        _taskQueue.Clear();
        LeftTask = null;
        RightTask = null;

        _sceneInteractor.ShutDown();
        Intel.ShutDown();
        Convoy.ShutDown();
        Moving.ShutDown();
        try { _harmony?.UnpatchSelf(); }
        catch (Exception ex) { MelonLogger.Error($"[FCS] UnpatchSelf failed: {ex}"); }
        _harmony = null;
    }

    /// <summary>
    /// 常驻后台协程：周期性检测装药余量（两炮共用池），低于阈值时自动购买一次装药卡。
    /// 购买必须持 _deskLock——采购台是共享硬件，与任务流程的采购互斥（阻塞等待，不破坏临界区）。
    /// 必须在 TryBind 成功后启动并登记进 _runningCoroutines；Dispose 时随其它协程一起 Stop，
    /// 迭代器被 Stop 时 Dispose 会执行 finally，锁不会泄漏。
    /// </summary>
    private IEnumerator ReplenishPowderLoop() {
        while (true) {
            yield return new WaitForSeconds(PowderCheckInterval);
            // 两炮共用一个装药余量池，读数应一致；取较小值保守触发。
            var charges = Math.Min(LeftGun.RemainingCharges(), RightGun.RemainingCharges());
            if (charges >= PowderReplenishThreshold) continue;
            MelonLogger.Msg(
                $"[FCS] AutoReplenish: powder charges {charges} < {PowderReplenishThreshold}, buying one");
            yield return _deskLock.Acquire();
            try {
                yield return _purchaseDeck.BuyPowders();
            }
            finally {
                _deskLock.Release();
            }
        }
    }

    public IEnumerator ExposeAllEntities() {
        while (true) {
            foreach (var m in MapTable.GetAllFireMissionEntities()) {
                var vr = m.transform.FindChild("VisualRoot");
                vr.gameObject.SetActive(true);
                vr.FindChild("Info").gameObject.SetActive(true);
            }
            yield return new WaitForSeconds(1f);
        }
    }

    /// <summary>
    /// 把任务加入调度队列。用户不指定炮管——调度器自动派给空闲炮管。
    /// 入队后立即尝试派发；若两管炮都忙，任务留在队列里，等某管炮打完自动拉取。
    /// 必须在主线程调用（点击回调即是）。
    /// </summary>
    public void EnqueueTask(ArtilleryTask task) {
        task.progress = Progress.Pending;
        _taskQueue.Enqueue(task);
        TryDispatch();
    }

    /// <summary>把队首任务派给空闲炮管，直到没有空闲炮管或队列空。</summary>
    private void TryDispatch() {
        while (_taskQueue.Count > 0) {
            LeftRight slot;
            if (LeftTask == null) slot = LeftRight.Left;
            else if (RightTask == null) slot = LeftRight.Right;
            else break; // 两管炮都忙

            var task = _taskQueue.Dequeue();
            if (slot == LeftRight.Left) LeftTask = task;
            else RightTask = task;
            StartTaskRoutine(slot, task);
        }
    }

    /// <summary>
    /// 启动一个火控任务协程。用 MelonCoroutines 跑协程实现延时——
    /// 协程由 Unity 在主线程分帧驱动，yield 期间不阻塞、恢复后仍在主线程，
    /// 因此可安全访问 IL2CPP 对象。绝不能用 async/Task.Delay：其 continuation
    /// 会在线程池线程恢复，跨线程访问 IL2CPP 运行时会导致进程崩溃且无日志。
    /// </summary>
    private void StartTaskRoutine(LeftRight leftRight, ArtilleryTask task) {
        var handle = MelonCoroutines.Start(RunTaskRoutine(leftRight, task));
        _runningCoroutines.Add(handle);
    }

    /// <summary>登记并启动一个协程（Dispose 时统一停止）。供 IntelSystem 等子系统使用。</summary>
    public object RunTracked(IEnumerator routine) {
        var handle = MelonCoroutines.Start(routine);
        _runningCoroutines.Add(handle);
        return handle;
    }

    /// <summary>征用台互斥锁（给 ConvoyAssist 自动打卡用）：与弹药采购/任务流程共用同一硬件。</summary>
    public IEnumerator AcquireDeskLock() => _deskLock.Acquire();
    /// <summary>释放征用台互斥锁。</summary>
    public void ReleaseDeskLock() => _deskLock.Release();

    /// <summary>炮管打完一发后释放槽位并尝试拉取队列里的下一个任务。</summary>
    private void ReleaseSlot(LeftRight leftRight) {
        if (leftRight == LeftRight.Left) LeftTask = null;
        else RightTask = null;
        TryDispatch();
    }

    private IEnumerator RunTaskRoutine(LeftRight leftRight, ArtilleryTask task) {
        var gunSys = leftRight == LeftRight.Left ? LeftGun : RightGun;

        // ===== 炮塔预约：任务一开始就在后台抢方向角并转向 =====
        // 方向旋转和装填/升仰角互不冲突。后台协程阻塞式抢炮塔锁（"一旦释放就立即获取"），
        // 一拿到就开始转向，与本任务接下来的整个装填+升仰角段重叠。等到击发前只需确认它转好，
        // 而不必等仰角转完再从头抢炮塔、再转向。方向角必须独占到这一发打出去为止，
        // 故锁一直持有到击发完成（WaitFire 后由 ReleaseOnce 归还）。
        var turret = new TurretReservation();
        // 独立的 fire-and-forget 协程，必须登记以便 Dispose 时一并 Stop，
        // 否则热重载后旧 ALC 的它仍被 Unity 驱动 → 崩溃。
        _runningCoroutines.Add(MelonCoroutines.Start(ReserveTurretAndRotate(task, turret)));
        
        var powderCount = _sceneInteractor.maxCharge ? 6 : BallisticCalculator.MinimumCharge(task.distance);

        // ===== 临界区 1：解算 =====
        // 弹道计算器 / 确认台 / 采购台都是全局唯一硬件，必须串行。算完仰角即放，
        // 让另一管炮能立刻进来算它自己的弹道，与本管炮接下来的长装填段重叠。
        float elevation = 0f;
        bool viable = true;
        yield return _deskLock.Acquire();
        try {
            task.progress = Progress.Calculating;
            yield return BallisticCalculator.SetDistance(task.distance);
            yield return BallisticCalculator.SetDirection(task.angel);
            yield return BallisticCalculator.SetCharge(powderCount);
            yield return BallisticCalculator.SetShellType(task.bulletType);
            yield return BallisticCalculator.Calculate();
            elevation = BallisticCalculator.GetElevation();

            // 装药不足则补购。单次采购未必补满（且偶发点击早于卡牌入槽而失败），
            // 故循环购买直到够本次发射所需，避免“装药不足但非 0”时直接推进、卡住后续装填。
            // 加购买次数上限兜底：采购始终无效时不至于无限循环（每次约 2.5s）。
            var powderPurchaseAttempts = 0;
            while (gunSys.RemainingCharges() < powderCount) {
                yield return _purchaseDeck.BuyPowders();
                if (++powderPurchaseAttempts >= 10) {
                    MelonLogger.Error(
                        $"[FCS] {leftRight} 炮管：购买装药 {powderPurchaseAttempts} 次后仍不足 " +
                        $"{powderCount}（当前 {gunSys.RemainingCharges()}），停止补购。");
                    break;
                }
            }

            task.progress = Progress.SelectingBullet;
            // 弹仓里没有目标弹种则采购（采购台也是共享硬件，放在锁内）。
            if (!gunSys.HaveBulletInCylinder(task.bulletType)) {
                if (!gunSys.HaveEmptyShellInCylinder()) {
                    task.progress = Progress.Failed;
                    viable = false;
                }
                else {
                    yield return _purchaseDeck.BuyShell(task.bulletType, leftRight);
                }
            }
        }
        finally {
            _deskLock.Release();
        }

        if (!viable) {
            // 任务不可行：取消炮塔预约并归还（后台若尚未抢到，会在抢到后自行归还），
            // 并释放炮管槽位让队列里的下一个任务能用这管炮。
            turret.Canceled = true;
            ReleaseTurretOnce(turret);
            ReleaseSlot(leftRight);
            yield break;
        }

        // ===== 锁外：装填（每管炮独立，最耗时段，可与另一管炮全程并行）=====
        task.progress = Progress.LoadingBullet;
        yield return gunSys.LoadBullet(task.bulletType);
        
        
        task.progress = Progress.LoadingPowder;
        yield return gunSys.LoadPowder(powderCount);
        task.progress = Progress.WaitLoading;
        while (!gunSys.CanFire()) {
            yield return new WaitForSeconds(1f);
        }

        // ===== 锁外：升仰角（每管炮独立，最耗时段之一）=====
        // 仰角杆是本管炮专属，不碰共享硬件；此时后台多半已把方向角转好。
        task.progress = Progress.Aiming;
        yield return gunSys.SetElevation(elevation);

        // ===== 临界区 2：击发 =====
        // 此处不再现抢炮塔——炮塔早已由后台预约持有。只等它转到位（通常已就绪，瞬间通过），
        // 然后确认+击发。炮塔锁一直由本任务持有，直到击发完成才归还。
        task.progress = Progress.WaitingForFire;
        while (!turret.Ready) {
            yield return null;
        }
        try {
            yield return TriggerConsole.ConfirmTask();
            yield return TriggerConsole.ConfirmBullet();
            yield return TriggerConsole.ConfirmRotation();
            yield return TriggerConsole.ConfirmElevation();
            yield return TriggerConsole.ReadyToFire();
            yield return TriggerConsole.Arm(leftRight);
            if (task.timed) {
                // 定时开火（移动目标）：待机到 命中时刻 − 飞行时间 再击发，与 AutoFire 开关无关
                task.progress = Progress.AwaitingStrikeTime;
                yield return WaitForStrikeMoment(gunSys, task);
                TriggerConsole.Fire();
                MelonLogger.Msg($"[FCS] 定时击发: {task.strikeLabel}");
            }
            else if (_sceneInteractor.AutoFire) {
                TriggerConsole.Fire();
            }
            yield return gunSys.WaitFire();
        }
        finally {
            ReleaseTurretOnce(turret);
        }

        // ===== 锁外：回位（仰角回 0，每管炮独立，最耗时段之一）=====
        task.progress = Progress.BackToIdle;
        yield return gunSys.WaitBackToIdle();
        task.progress = Progress.Finished;
        _sceneInteractor.TaskFinished(task);
        // 释放炮管槽位，自动拉取队列里的下一个任务。
        ReleaseSlot(leftRight);
    }

    /// <summary>
    /// 炮塔预约状态。三个标志全在主线程协作式调度下读写，无真正并发。
    /// 生命周期：后台 <see cref="ReserveTurretAndRotate"/> 抢锁→转向→置 Ready；
    /// 主流程击发后 / 任务放弃时 ReleaseTurretOnce 归还。Released 保证恰好归还一次。
    /// </summary>
    private sealed class TurretReservation {
        public bool Acquired;  // 已拿到炮塔锁
        public bool Ready;     // 已转到目标方向角
        public bool Canceled;  // 主流程已放弃本次预约
        public bool Released;  // 锁已归还（防重复 Release）
    }

    /// <summary>
    /// 后台预约炮塔并转向。阻塞式抢锁实现"一旦炮塔释放就立即获取"。
    /// 抢到后若发现已被取消则立即归还、不空转；否则转到目标方向并置 Ready，
    /// 此后炮塔由主流程在击发完成时归还（若转向期间被取消则在此自行归还）。
    /// </summary>
    private IEnumerator ReserveTurretAndRotate(ArtilleryTask task, TurretReservation res) {
        yield return _turretLock.Acquire();
        res.Acquired = true;
        if (res.Canceled) {
            ReleaseTurretOnce(res);
            yield break;
        }
        yield return Turret.SetRotation(task.angel);
        res.Ready = true;
        // 转向期间主流程可能已放弃（如解算失败）——此时主流程不会再击发，由这里归还。
        if (res.Canceled) {
            ReleaseTurretOnce(res);
        }
    }

    /// <summary>
    /// 定时开火的待机循环：持续读 战场时钟 与 预测飞行时间（含 fireDelay），
    /// 到 命中时刻 − 飞行时间 返回（调用方立即击发）。轮询间隔随剩余时间减半逼近，
    /// 末段精度 ~0.05s。飞行时间读数无效（0）时退化为按预定时刻直接击发并告警。
    /// </summary>
    private IEnumerator WaitForStrikeMoment(GunSystem gunSys, ArtilleryTask task) {
        var logged = false;
        var zeroFlightWarned = false;
        while (true) {
            var now = Moving.ScenarioNow();
            if (now < 0f) {
                MelonLogger.Error("[FCS] 定时开火：任务时钟丢失，立即击发");
                yield break;
            }
            var flight = gunSys.PredictedFlightSeconds();
            var remain = task.strikeTime - now;
            if (!logged) {
                MelonLogger.Msg($"[FCS] 定时开火待机: {task.strikeLabel} 剩余 {remain:F1}s 飞行时间 {flight:F1}s");
                logged = true;
            }
            if (flight > 0f) {
                if (remain <= flight) yield break;
            }
            else {
                if (!zeroFlightWarned) {
                    MelonLogger.Warning("[FCS] 定时开火：飞行时间读数为 0（预测未就绪？），将按预定时刻直接击发");
                    zeroFlightWarned = true;
                }
                if (remain <= 0.2f) yield break;
            }
            yield return new WaitForSeconds(Mathf.Clamp((remain - flight) * 0.5f, 0.05f, 1f));
        }
    }

    /// <summary>归还炮塔锁，保证恰好一次。仅在确实持有（Acquired）且未归还时执行。</summary>
    private void ReleaseTurretOnce(TurretReservation res) {
        if (res.Acquired && !res.Released) {
            res.Released = true;
            _turretLock.Release();
        }
    }
}
