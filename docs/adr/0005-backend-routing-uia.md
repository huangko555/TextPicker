# ADR-0005 后端路由与 UIA 读取顺序

**状态**：Accepted（v6.1 定案，勿翻案）
**Phase**：2（seam） / 3（UIA 后端）

## 背景

冻结的 UIA 读取顺序为 v6.1 定案且经对抗审查确认可靠；app-specific 判断必须与后端机制解耦。

## 决策

- `BackendRouter` + profile 表 seam（v1 仅 UIA profile）；profile 声明后端顺序 / 允许手势 / 超时。app-specific 判断不得散落进 UIA 代码。
- UIA 读取顺序：`ElementFromPoint(mouseUp)` + `GetFocusedElement` → 密码双查（命中 + 焦点元素）→ 命中/焦点**父链**有界上溯找 TextPattern（找到即停，非固定宽度 BFS）→ `GetSelection()` 首个非退化 range；ranges.Count>1 → `MultipleSelectionUnsupported` → `GetText(Max+1)` 截断检测 → LocalContext 扩展（ContextKind 标注退化）→ `UiaWholeValue` 受限路径。
- 几何 = range 全量 `GetBoundingRectangles`（SAFEARRAY，count%4==0 校验）+ 两端折叠矩形 + 方向判定。
- PID 判序：**按下窗口 PID 必须 == 读取时焦点 PID**；点命中元素仅在其 PID 匹配时优先，否则走焦点链；focus≠down → `ProcessMismatch` 拒绝。
- ValuePattern（`UiaWholeValue`）：默认关；开启仅限 Ctrl+A/Probe 且无 TextPattern 元素；携带 IsReadOnly；结果标 WholeValue。

## 实现记录

- [x] Phase 0：`ISelectionBackend` seam（BackendReadRequest/Result，epoch 携带）+ 测试假件；`UiaWholeValue`/`BackendRouter` 类型位已留
- [ ] Phase 3：`UiaSelectionBackend` + `BackendRouter` + profile 表
