# ADR-0004 方向感知锚点

**状态**：Accepted（v6.1 定案，勿翻案）
**Phase**：3

## 背景

划词按钮需要出现在用户选区的**结束端**（caret 端）附近，与选择方向有关；`CompareEndpoints` 距离值不可作绝对偏移，只有正负号可靠。

## 决策

- 权威源 = `TextPattern2.GetCaretRange` + 与选区 Start/End `CompareEndpoints`（只用正负号）判方向；**VK 仅 DirectionHint**（例：Shift+Left 在正向选区上是缩 End，不是跳 Start——勿用 VK 推定端点）。
- 鼠标触发兜底：两端折叠矩形中取离 mouseUp 近者，Direction 置 null。
- 无权威来源 → Direction=null，**绝不猜**。
- 锚点链：caret 端点矩形（单个、宽高 ≥6px、mouseUp 在外扩 50px 内；键盘手势跳过 50px 校验）→ mouseUp 点 → fallbackAnchor（仅显式捕获）→ 无锚点发布。**锚点失败不抹掉文本**。
- 屏幕外拒绝 = 锚点级（完全在显示器外），非整选区级。

## 实现记录

- [ ] Phase 3：锚点链实现 + 端点折叠矩形 + 方向判定
