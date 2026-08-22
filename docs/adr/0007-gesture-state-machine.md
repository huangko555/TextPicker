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

- [ ] Phase 1：状态机全路径单测（五手势 + Explicit、过期、取消、Esc 打断、id 单调、三击取代、双击拖拽合成、Ctrl+Shift 变体、相位序列、过滤无洞）
