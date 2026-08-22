# ADR-0002 RunEpoch 防迟到回调污染

**状态**：Accepted（v6.1 定案，勿翻案）
**Phase**：0（门面骨架） / 2（lane 接线）

## 背景

UIA 事件 handler remove 后**仍可能有迟到回调**（微软文档与 InputCue 生产佐证）；「Start#1 → Stop → Start#2 被 #1 迟到回调污染」是真实风险。

## 决策

- 每次 `Start()` 递增内部 epoch；所有 UIA / RawInput 入队消息携带 epoch。
- Stop 后与下次 Start 后的旧 epoch 回调**直接丢弃**。
- UIA handler remove 后不立即销毁 COM handler 对象（迟到回调可能访问已释放对象）。

## 后果

Phase 0 契约测试 #7 锚定：Stop → 注入迟到回调 → Start → 旧 epoch 回调不产生任何公开事件。

## 实现记录

- [x] Phase 0：门面 epoch 计数（Start 递增）+ 手势/后端结果/UIA 信号三处旧 epoch 丢弃 + 契约测试 #7；Stop() 同步终结在飞 generation 为 Failed(Cancelled) 以保持 exactly-one-terminal 跨 Stop/Start
- [ ] Phase 2：UIA 事件 lane（Lane 1）与 RawInput 入队统一携带 epoch；handler remove 后延迟销毁 COM handler
