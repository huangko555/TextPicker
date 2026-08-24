# TextPicker

Windows「选区观察」共享深模块（C# / .NET 10）：检测任意应用中的文本选择手势（拖选 / 双击 / 多击 / Shift+点击 / Ctrl+A / Shift+方向键），经 UI Automation 读取选区内容与几何，方向感知锚点，两阶段发布（先候选后正文），不可变结果。附带一个 WPF 调试面板作为第一个消费者。

## 硬约束（不可协商）

1. **零剪贴板代码**。UIA-only，取不到就安静失败。调试面板用 `GetClipboardSequenceNumber` 自证零触碰。
2. **深模块**：输入线程、隐藏窗口、COM 线程、串行队列、generation 生命周期、UIA 事件订阅、捕获后失效跟踪全部自持；对外一个门面接口 `ISelectionPicker`。
3. 诊断与日志**结构上不可能携带选区正文**。
4. v1 交付物 = 模块 + 接口 + 调试面板，不做翻译 / 复制等下游功能。

## 项目结构

```
src/
  TextPicker.Core/     契约、手势状态机、TextNormalizer、几何解析、身份/仲裁模型（纯 net10.0）
  TextPicker.Windows/  输入源 seam、caret 探针链、后端路由、三 lane 执行器、SelectionPicker 门面
  TextPicker.App/      WPF 调试面板（Phase 4 加入）
  TextPicker.SmokeRunner/  Windows 实机烟测跑批器（会打开应用并注入鼠标键盘输入）
tests/
  TextPicker.Core.Tests/
  TextPicker.Windows.Tests/
```

## 文档导航

- [docs/roadmap.md](docs/roadmap.md) — Phase 0–5 执行顺序、过门记录、冒烟矩阵三态表
- [docs/adr/](docs/adr/) — 架构决策记录（ADR-0001..0008 为冻结决策）

## 构建与测试

```
dotnet build TextPicker.slnx -c Debug
dotnet test TextPicker.slnx
```

实机烟测可按场景运行，例如 `dotnet run --project src/TextPicker.SmokeRunner -- notepad`。`notepad-keyboard`、`dpi`、`chrome-iframe`、`edge`、`word` 使用人工操作提示；`admin` 会触发 UAC，并用于复现当前无 `uiAccess` 模式对管理员窗口的 Known-bad 边界。这些人工场景均不会被 `all` 自动运行，其余可用场景见 `--help`。

x64、`TreatWarningsAsErrors`、.NET 10（SDK 10.0.302，见 global.json）。

## 证据基础

交互阈值与时序常量为 v6.1 设计案冻结的标定值；平台事实（UIA/Raw Input 行为）有微软文档与 InputCue 生产代码佐证。目标消费者为 Peeko（划词翻译）与 InputCue（输入状态提示），v1 不接入。
