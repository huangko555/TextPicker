# ADR-0008 隐私与诊断

**状态**：Accepted（v6.1 定案，勿翻案）
**Phase**：0（结构保证） / 4（面板打码）

## 决策

- Diagnostics / Counters / 日志**结构上不可能携带正文**：类型层面无 string 正文字段（Diagnostics 事件参数不含任何自由文本字段）。
- 键盘输入只在状态机内分类为手势，任何 API / 事件 / 诊断不暴露按键流。
- 调试面板正文默认打码（长度 + 哈希），显式开关揭示，**永不落盘**。
- 多客户端互斥（同类划词产品的跨进程协调先例）：v1 仅预留 seam（Options 选项位），机制后续。

## 后果

Phase 0 起 `SelectionDiagnosticsEventArgs` / `SelectionPickerCounters` 类型保持 string-free；契约测试可加结构扫描守卫。

## 实现记录

- [ ] Phase 0：诊断参数与计数器类型 string-free 落码
- [ ] Phase 4：面板打码 / 揭示开关 / 永不落盘
