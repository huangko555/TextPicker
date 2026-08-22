# ADR-0007 手势状态机（Core 纯逻辑）

**状态**：Accepted（v6.1 定案，勿翻案）
**Phase**：0（DTO） / 1（状态机）

## 决策

- 消费归一化未分类输入记录（ADR-1 注入 DTO）；注入 `TimeProvider` + `GetDoubleClickTime`（测试注入定值）。
- 框选位移阈值：X 或 Y 任一 ≥6px。
- 多击 = 系统双击时间（`GetDoubleClickTime`）+ 按下点 6px 容差；**双击+拖拽 = 单词扩展选择，合成单个 MultiClick 手势**；三击取代双击 = `SelectionSuperseded`（预期行为，文档化）。
- Ctrl+A：A 键**抬起**且 Ctrl 按下（`GetAsyncKeyState` 查 Ctrl）。
- Shift 键盘选择：虚拟键 0x21..0x28（PageUp/PageDown/End/Home/四方向），含 Ctrl 修饰变体（如 Ctrl+Shift+Home）。
- 打断 = 滚轮 / 右键 / 中键 / Esc；Hook 消息 >1s 丢弃（时钟用 `GetMessageTime()`）；候选 501ms 未完成取消。
- 键盘事件只做手势分类后即弃，不保留按键记录；手势层过滤（消费者窗口 / 全屏 / OwnProcess / 排除列表 / 无效输入）静默完成，**不产生 generation**（§4 无洞不变式）。

## 实现记录

- [x] Phase 0：归一化输入 DTO（InputRecord：Key/PointerDown/PointerUp/PointerWheel + ModifierSnapshot + ForegroundTargetSnapshot + MessageTimeMs）
- [x] Phase 1：`GestureStateMachine`（消息时间钟域、可注入双击时间）+ 全路径单测（五手势、双击拖拽合成、三击连发、Ctrl+Shift 变体、过期、打断、无效序列、Reset）+ `CoreGestureFeed` 适配器 + 8 种子 property-based 不变式测试
- [x] Phase 1 局部决策：键盘手势统一取**抬起沿**（Ctrl+A 定案在案；Shift 键盘选择从之，自动重复不重复触发）；手势开关过滤不在状态机内（门面策略层单一过滤源）；打断在候选在飞时 → Failed(Interrupted)（捕获完成后的 Esc/点外 → Invalidated，Phase 2 接线）
