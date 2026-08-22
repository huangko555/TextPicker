# ADR-0003 三 lane + COM 隔离

**状态**：Accepted（v6.1 定案，勿翻案）
**Phase**：2

## 背景

跨进程 UIA 调用需独立 MTA；事件 add/remove 集中同一线程；remote UIA 对象生命周期与创建它的 apartment 绑定（COM 对象跨 apartment 传递会失效或引发 RCW 问题）。

## 决策

- Lane 1：稳定 UIA 事件注册 MTA 线程（事件 add/remove 同线程）。
- Lane 2：**Capture lane**（串行 MTA）。
- Lane 3：**Observer lane**（caret / probe / state / content-stream 共用）。
- **COM 对象不过 lane**：`IUIAutomationElement` / `IUIAutomationTextRange` / pattern 接口不得进入 Channel、EventArgs、跨 lane DTO；每 lane 各自持有独立 `IUIAutomation` 实例；跨 lane 只传 PID/HWND/事件 id/时间戳/generation/纯数据，下游 lane 自行 reacquire。
- 调度优先级：手势捕获 > 显式捕获 > caret/probe/state > 流节拍；流节拍永不挤掉捕获；流在飞上限 1；有限队列 + DropOldest（同类内）；键盘连发 40ms 合并最新获胜。

## 后果

Phase 0 契约测试 #6（结构测试：跨 lane DTO 白名单不含 COM 接口）在本 ADR 落地前即武装扫描规则。

## 实现记录

- [x] Phase 0：`ILaneTransferable` 标记 + `LaneDtoRules` 扫描器（识别 CsWin32 [ComImport] 接口）+ 契约测试 #6（含合成违规 DTO 自证扫描器有效）
- [ ] Phase 2：三 lane 执行器 + 调度优先级（手势捕获 > 显式 > caret/probe/state > 流节拍）+ DropOldest + 40ms 键盘合并
