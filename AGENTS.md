# AGENTS.md — IronNestFCS（fork 开发笔记）

> 面向后续接手开发的 agent/开发者。用户向功能说明见 README.md。
> 本文件记录架构地图与**实测硬事实**——这些都是日志逐行复盘换来的，改动前先读。

## 仓库与工作流

- fork：`github.com/19338126485/IronNestFCS`（remote `fork`）；上游 `origin` = svr2kos2/IronNestFCS。
- 本机游戏目录：`D:/game/IronSimulator_B24642854/Iron Nest Heavy Turret Simulator`（其 AGENTS.md 有游戏本体逆向资料：存档 INSV 格式、il2cpp 转储位置等）。
- 构建：`dotnet build IronNestFCS.Logic/IronNestFCS.Logic.csproj -c Release`，输出直落游戏 `UserData/IronNestFCS/`，游戏内 **F9** 热重载生效。只构建 Logic 即可，宿主不动。
- 三个 `.csproj` 的 `GameDir` 是本机路径，**刻意不提交**；`git add` 用明确文件名，别 `-A`。
- 日志：游戏目录 `MelonLoader/Latest.log`（每次启动重写，F9 重载会续写）。模组输出前缀：`[FCS]` 火控、`[Intel]` 情报测绘、`[Convoy]`/`[Convoy.Diag]` 卡牌辅助。
- IL2CPP 转储：`D:/game/IronSimulator_B24642854/dump/Dump0/DiffableCs/Assembly-CSharp/*.cs`（逐类签名，无方法体）；`stringliteral.json` 可查硬编码字符串（中文报文在本地化资产里，查不到）。

## 子系统地图（IronNestFCS.Logic/FCS/）

- `IntelSystem.cs`（核心）：情报哈希轮询（3s）→ `IntelParser` 解析 → `GeoSolver` 几何解算 →
  候选登记簿（按名去重、原地更新、保留用户状态）→ 3D 标记环/标签 → 落子/开火/铁巢归位。
  每个"阶段"的职能见 `BuildCandidateResults` 注释（阶段 1 gridref / 2 主题链式 / 2.5 车队 / 2.6 FO / 3 散条目）。
- `IntelParser.cs`：全部报文正则。每种格式的实测样本都写在注释里；**新格式先加正则，
  再用日志原文离线验证**（python 快速模拟）。含数字却匹配不上的行会自动进日志。
- `GeoSolver.cs`：方位线/距离圆求交、残差投票、低置信度（交角 |sin|<0.35 或 Fuzzy 罗盘词）。
  擦边未交容差 0.2km（报文舍入是必然事件，曾差 2m 丢目标）。
- `ConvoyAssist.cs`：紧急转移检测（图标位移）、自动打位置报告卡、NestGPS 作弊直读、卡牌侦察 dump。
- `FcsSceneInteractor.cs`：3D 按钮排（鼠标锁定屏幕中心，一切交互走准星射线点击，不能用 IMGUI 按钮）。
- `FSC.cs`：任务调度 + 协程登记 + 桌面锁（征用台硬件互斥）。

## 实测硬事实（违反任何一条都出过 bug）

- **坐标系**：实体 `MapEntity.Position` 的 x/y 即网格公里（列=A+floor(x)，行=floor(y)+1，1格=1km，图 20×10）。
  报文网格格值取**子格中心**（+0.05km，三组实体对照精确验证）。
- **网格→桌面**：实体图标层是 3D 倾斜平面，必须用全实体最小二乘仿射拟合（x/y/z 三分量）；
  正逆变换往返误差 ~0.08km。`FireMission.ToLocalSpace` 返回垃圾，禁用。
- **纸带是倒序的**：电报机把新内容打在**顶部**；且会滚动丢弃旧行、会重印。
  一切依赖行位置/条数/文本变化的设计都被证伪过——跨 Survey 的新旧分界唯一可靠依据是
  **"这一行以前见过没有"**（seenConvoyLines 模式）。
- **多解歧义必须沿锚点链传播**：上游主题双解时下游每个分支各解一次，否则真解会丢
  （巡洋舰案例）。图外交点一律假解剪除。唯一匹配友方实体 = 地面真值（锚点优先、分支确认）；
  敌方实体绝不用于解算（那是 Reveal/NestGPS 的活）。
- **罗盘方位词（西北偏西等）±11°**：只产生交点、**不参与残差投票**（会绑架排序）。
- **卡牌**：`RequisitionSlot.PlaceCard/AttemptRequisition` 程序化打牌；入槽走物理移动
  （PurchaseDeck 同款坐标）；玩家打过的卡会**留在槽里**（要先弹出）；坐标参数经
  控制台 `PunchcardVariable`(Coordinate) 的 L/N setter。
- **转移是间歇性步进滑动**（步间停 3~4s），静止判定要 8s；炮位真实位置唯一可靠来源是
  `TurretLocationIcon` 的世界坐标（`TurretController.transform.position` 恒定，是假的）。
- **触发逻辑不能依赖报文到达**：滑动停稳后可能再无新文本，评估必须在轮询里做。
- **热重载规则**：不注册新 IL2CPP 类型、不持静态 IL2CPP 引用、协程经 `FSC.RunTracked` 登记、
  ShutDown 清空引用。

## 铁路/移动目标（已实现，2026-08-13）

- `MovingTargets.cs`：列车（总站+轨道方位角+时刻表拟合匀速）与舰船（目击点+航向+航速节）
  的行进路线推算，登记为自动推进位置的候选（»前缀、青色标记环，每秒重算，图外隐藏）。
- **游戏时钟 = `MissionStatsTracker.Instance.mission.missionTime`（秒）**：报文 T= 时刻就是
  `[TIMER <MissionTime>]` 打印的它。ClockCheck 会对每个事件行打"打印延迟"日志校验此假设。
- **飞行时间 = `GunController.PredictedImpactTime + fireDelay`**（调炮后自动更新，手表同源）。
- 对移动目标按 Fire = 定时开火：打击时刻 = max(now+150s 装填预算, 进图时刻)，钳在可交战
  窗口内；任务装填调炮完成后待机（`Progress.AwaitingStrikeTime`），到 命中时刻−飞行时间
  自动击发（轮询间隔随剩余时间减半，末段 ~0.05s）。飞行时间读数为 0 时退化为按时刻直接打。
- 报文模板（resources.assets 本地化串实测）：列车"路径点-A - 距车站6.00km：T=10:06:50"、
  "- 10:06:50 - 机车经过 路径点-A：P6 0:4。"（实测点精化+时钟校验）、"列车停止中 - T"+
  "距总站 6.00km。"（停车）；舰船"<船名>已发现："+"在 T:.. 时经过 <格>。"+"以9.7节速度
  航行12°航向。"（1节=1.852km/h，模板自证 9.7节=20秒0.10千米）。"已确认摧毁 <名>"除名。
- 已知边界：打击诸元在入队时按当时炮位解算，紧急转移后不会重算；舰船新目击会整条路线
  重置（取最新目击）；列车多解歧义不存在（路线完全确定）。
