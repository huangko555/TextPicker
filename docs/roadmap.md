# 执行路线图

执行顺序的权威来源（自 v6.1 设计案 §8.2 迁移，设计稿原件在仓库外留存）。每个 Phase 结束为过门点：汇报测试结果与关键决策，确认后进下一个。执行中出现的新问题按证据局部决策并记 ADR，只在范围变更时升级。

## Phase 0 — 契约冻结测试（7 项，先于一切实现，全部通过才继续）

| # | 测试 | 验收 |
|---|---|---|
| 1 | Raw Input 所有权 | 同进程双注册冲突复现（后注册者抢占）；注入模式无冲突；Owned fail-fast（`GetRegisteredRawInputDevices` 检测到非自身注册时抛 `RawInputRegistrationConflict`，不覆盖） |
| 2 | 强类型 ID + 显式捕获语义 | `CaptureCurrentSelectionAsync` 完成后 `LastCapture` 不变、无 `SelectionCaptured` 事件、手势状态不变 |
| 3 | CandidateReady 零 UIA | 假后端 sleep，CandidateReady 必须立即到达（`CandidateTargetSnapshot` 结构上无 UIA 字段） |
| 4 | 几何可空 | 正文成功 + `GeometryCompleteness.None` 合法发布，不判失败 |
| 5 | exactly-one terminal | ∀ generation：CandidateReady==1、terminal 互斥唯一、Captured 后 Invalidated≤1、被过滤手势不产生 generation（无洞） |
| 6 | 三 lane COM 隔离 | 结构测试：跨 lane DTO 类型白名单不含任何 COM 接口 |
| 7 | RunEpoch | Stop → 注入迟到 UIA 回调 → Start → 旧 epoch 回调不产生任何公开事件 |

注：Phase 0 的门面为**最小实现**（seam 可注入），Phase 1–2 用真实状态机 / 输入源 / lane 执行器替换内部件，7 项测试持续作为回归守卫。

## Phase 1 — Core

契约类型 + 手势状态机（合成输入记录流驱动）+ TextNormalizer + 几何解析 + 身份仲裁 + 全部单测（含 5 号不变式的 property-based 全路径、仲裁黄金迹线回放）。

## Phase 2 — 输入与门面

自持隐藏窗口 WM_INPUT 现场冒烟（方案唯一需现场验证的技术假设）→ `OwnedRawInputSource`（含 fail-fast）→ `FocusTargetEventSource` → 三 lane 执行器 → 门面串行队列 / 仲裁 / 失效跟踪 / 豁免表 → `TargetPolicy`（含全屏检测）。

## Phase 3 — UIA

`UiaSelectionBackend`（内容 / LocalContext / 几何 / 方向锚点 / 新鲜度证据 / 确认读）+ `CaretProbeChain`（无 TSF 四级）+ `BackendRouter` seam + Windows 测试（进程内 WPF 宿主，异步断言不阻塞 UI 线程）。

## Phase 4 — 调试面板

PMv2 manifest；常驻区（指针 / 手势相位 / 焦点目标卡片 / caret+来源 / 剪贴板序列号 / Counters / 全屏暂停状态 / 宿主 DPI 感知警告）；事件流（Diagnostics 阶段行 + Captured/Failed 完整字段，正文默认打码）；交互区（五手势开关、排除进程、Probe includeText、AllowWholeValueBackend / 内容流订阅 / EnrichSurrounding / PauseWhenFullScreen、RegisterConsumerWindow 演示）。

## Phase 5 — 全量验证

`dotnet test` 全绿 + 冒烟矩阵（真实机器，真人手势操作，结果填入下方三态表）。

## 过门记录

| Phase | 日期 | 结果 | 备注 |
|---|---|---|---|
| 0 | 2026-08-23 | ✅ 17/17 全绿（Core 4 + Windows 13） | 7 项契约冻结测试全部落地并通过；Raw Input 双注册冲突在本机复现（后注册者抢占 + fail-fast）；门面为 seam 注入的最小实现，Phase 1–2 替换内部件后测试持续作回归守卫。局部决策见下。 |
| 1 | 2026-08-23 | ✅ 84/84 全绿（Core 59 + Windows 25） | 状态机/TextNormalizer/GeometryBuilder/Arbiter/Options 校验 + 黄金迹线回放 + property-based 不变式（8 种子）。局部决策见下。 |
| 2 | 2026-08-23 | ✅ 102/102 全绿（Core 59 + Windows 43） | OwnedRawInputSource（自持线程+隐藏窗口+INPUTSINK+fail-fast）+ WM_INPUT 现场冒烟（SendInput 合成键盘实测可达）+ QueryRunner（超时/熔断/卡死置换/孤儿上限/quarantine）+ LaneRoutedBackend + 门面失效跟踪（Esc/点外/前台/TargetGone + 消费者豁免）+ WinEvent 焦点源 + 全屏暂停。局部决策见下。 |
| 3 | 2026-08-23 | ✅ 111/111 全绿（Core 59 + Windows 52） | UiaSelectionBackend（ADR-5 全链：PID 判序/密码双查/父链/多 range 拒绝/截断/LocalContext/几何/方向权威/锚点链）+ CaretProbeChain 三级 + 托管 UIA 事件源（Lane 1）+ 观察者 lane（caret/state/probe/内容流）+ 进程内 WPF 宿主端到端 9 测试。局部决策见下。 |
| 4 | 2026-08-23 | ✅ 111/111 全绿 + 面板启动冒烟通过 | WPF 调试面板（PMv2 manifest；常驻区：指针/相位/焦点/caret/剪贴板序列号/全屏/Counters；事件流：正文默认打码 sha256+长度、揭示开关永不落盘；交互区：五手势开关/排除进程/Probe/选项热更/内容流订阅/消费者窗口演示）。|

### Phase 1 执行期局部决策

1. **键盘手势统一取抬起沿**：Ctrl+A 抬起触发为定案；Shift 键盘选择（VK 0x21..0x28）同样取 key-up——计划未明说，从一致性选择；key-down 自动重复不会重复触发。
2. **IncompleteTimeout（500ms，实测标定 ≈501ms 经决策取整）落在门面候选外预算**：手势时刻起，读+settle 未按期完成 → `Failed(IncompleteTimeout)`、迟到结果丢弃（「已标记但迟迟未完成的选择取消」语义）；状态机保持纯消息时间钟域、无真实时间依赖。
3. **打断动作双层语义**：候选在飞时 → `Failed(Interrupted)`（有 generation）；捕获完成后 Esc/点外/前台切换 → `Invalidated`（Phase 2 失效跟踪接线）。
4. **状态机不做手势开关过滤**：单一过滤源在门面策略层（DefaultTargetPolicy），状态机输出全部已分类手势。
5. **Arbiter 建模为双 lane**：Capture（手势 > 显式，串行）+ Observer（观察 > 流节拍，串行；流在飞上限 1 由 lane 串行天然成立）；40ms 合并窗口仅作用于同类未启动可合并项。
6. **GestureDropReason 公开枚举**进 `SelectionPickerCounters.GestureDropsByReason`（过期/打断清态/无效序列计数，string-free）。
7. **TextNormalizer 保留 `\t`**：Control 类别但属语义空白；`\n` 同理保留，其余 Control/Format（含零宽字符）滤除。
8. **CsWin32**：`GetWindowRect` 入清单（阶段一浅快照窗口矩形）。

### Phase 0 执行期局部决策（依 §11 授权：按证据局部决策并记录）

1. **数据记录用 `{ get; init; }` 属性而非公共字段**：计划 §5/§6 全码用了字段写法，但 §1 明文「不可变结果」——以属性落实不可变意图，类型名/成员名/枚举值与计划一致。`SelectionPickerOptions` 例外（按计划可变 + 整体替换）。
2. **CsWin32 清单**：`GetProcessDpiAwarenessContext`/`GetDpiForProcess` 在 CsWin32 0.3.298 元数据中不存在，暂不列入；Phase 2 DPI 防护时另选（如 `GetWindowDpiAwarenessContext`）。
3. **Stop() 终结在飞 generation 为 Failed(Cancelled)**：使 exactly-one-terminal 不变式跨 Stop/Start 严格成立（否则 Stop 边界出现无 terminal 的 generation）。迟到回调仍完全静默（契约 #7）。
4. **被过滤手势完全静默**：不发 Diagnostics 事件（§4「静默完成」从严解释），计数器含 FilteredGestures 供诊断；`CaptureFailureReason.OwnProcess/ExcludedProcess` 枚举值保留给显式/Probe 路径。
5. **显式捕获未运行时抛 InvalidOperationException**、**Start 重复调用抛异常**：计划未规定，按误用处理（fail-fast）。
6. **CancelRequest 使查询 Task 以 Fail(Cancelled) 收口**（不悬挂）；COM 在飞调用不中止，符合「放弃等待而非中止 COM」。
7. **新会话入口**：本表 + ADR + README 即入口；v6.1 设计稿原件在仓库外留存（不入仓）。

## 冒烟矩阵（三态：Known-good / Known-bad / Unknown，附版本号）

环境：本机双显示器（主 2560×1440 @100%，副 2560×1440 物理 @150%）；Chrome 151.0.4129.93；Edge 151.0.7922.173；Office16 Word。

| 场景 | 预期 | 结果 | 版本 |
|---|---|---|---|
| 记事本：拖选 / 双击 / 三击 / Ctrl+A / Shift+方向键 / 反向多行 | Captured，剪贴板序列号不变，锚点在释放端 | | |
| Word：同上 + Ctrl+Shift+Home | 同上 | | |
| Chrome 纯文本页 | 实测填三态（138+ 原生 UIA） | | |
| Chrome 标准输入框 / contenteditable | 实测填三态 | | |
| Chrome：跨域 iframe、Google Docs、PDF 查看器 | 实测填三态，不预设失败 | | |
| Edge：同 Chrome 子集 | 实测填三态 | | |
| 管理员记事本 | AccessDenied 可诊断 | | |
| 全屏视频 / 游戏 | 自动暂停 | | |
| 调试面板自身窗口划选 | 不产生候选（OwnProcess） | | |
| RegisterConsumerWindow 演示：点击消费者按钮 | 不打断当前候选 | | |
| 跨 DPI 显示器（主 100% ↔ 副 150%）选择 | 锚点在结束端附近且不越工作区 | | |

### Phase 2 执行期局部决策

1. **SendInput 合成输入可达 Raw Input**（冒烟实测）：自持隐藏顶级窗口 + WM_INPUT 泵假设成立；合成输入可入自动化测试。Phase 5 冒烟仍以真人手势为准。
2. **Capture lane 串行实现**：QueryRunner 对重叠请求快速失败（WorkerBusy）；LaneRoutedBackend 以信号量实现 FIFO 串行。手势 > 显式优先级调度由 Arbiter 模型承接（v1 FIFO，完整接入留后续）。
3. **Owned 注册用 RIDEV_INPUTSINK**（后台无焦点接收的必要条件）；fail-fast 在窗口创建后、注册前执行。
4. **纯移动 WM_INPUT 不翻译不上抛**（无按键/滚轮位时直接跳过，减轻状态机负担）。
5. **FocusTarget v1 = Win32 浅上下文**（PID/进程名/HWND/窗口类）；UIA 富化（ControlType/FrameworkId 等）在 Phase 3。
6. **消息时间 32 位回绕**（~49.7 天边界）可能产生一次性误判，接受并文档注明。
7. **消费者窗口豁免同时覆盖候选与失效**（点工具条/拖工具条既不产生候选也不触发 OutsideClick/ForegroundChanged 失效）。

### Phase 3 执行期局部决策

1. **UIA 事件源用托管 System.Windows.Automation**（InputCue 生产同路线）：原始 COM 客户端 API 没有 TextSelectionChanged 的整型事件 ID；读取路径仍为原始 COM（COM 不过 lane 纪律不变）。Windows 库为此引入 WPF FrameworkReference。
2. **显式捕获不做 PID 判序**：显式查询没有手势按下快照可复核，采纳焦点元素 PID；ProcessMismatch 保留给手势路径。
3. **caret 探针链 MSAA 第四级暂缓**：前三级（TextPattern2 → 折叠 TextRange → GetGUIThreadInfo+ClientToScreen）覆盖主流应用；冒烟矩阵证明需要时再补（记 ADR-0004 项下）。
4. **方向判定 caret 矩形严格要求 4 值**（单个矩形，§3.1 校验）；异常形态放弃权威判定而非降级猜测。
5. **Probe 的 includeText 语义 = 读当前选区文本**（非控件全文）。
