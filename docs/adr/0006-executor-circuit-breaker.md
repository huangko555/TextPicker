# ADR-0006 执行器与熔断

**状态**：Accepted（v6.1 定案，勿翻案）
**Phase**：2

## 背景

跨进程 UIA 调用可能卡死（无响应进程）；COM 同步调用不可安全中断。

## 决策

- 每 lane 一个 `UiaQueryRunner`（改编 InputCue `ObservationQueryRunner`）：MTA 请求-应答 worker、250ms 超时、2s 熔断。
- **卡死置换**：worker 超时且过 1 个冷却期仍未完成 → 遗弃线程、换新 worker（结果按 id 丢弃，无害）。
- **孤儿 worker 上限每 lane 各 1**（保住 Capture lane 置换额度）；达限后对目标 PID/HWND quarantine（冷却 + 跳过），不再造线程。

## 实现记录

- [ ] Phase 2：三 lane 执行器 + 置换 + quarantine + 测试（TimeProvider 注入）
