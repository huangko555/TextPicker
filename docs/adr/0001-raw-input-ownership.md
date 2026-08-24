# ADR-0001 Raw Input 注册权（owned / injected / broker 三模式）

**状态**：Accepted（v6.1 定案，三轮外部审查终判 Go，勿翻案）
**Phase**：0（fail-fast 部分） / 2（完整）

## 背景

微软明文：Raw Input 每设备类**进程内只能有一个注册窗口**（最后一次调用者生效），且库不应自行 `RegisterRawInputDevices`。InputCue 等宿主已有自己的 Raw Input 注册，模块若自行注册会相互抢占。

## 决策

- 内部 seam `IInputEventSource`；默认 `OwnedRawInputSource`：`CreateWindowEx` 隐藏**顶级**窗口 + 自有线程消息泵（WM_INPUT 的 `hwndTarget` 必须是顶级窗口，message-only 不在文档化契约内）。
- **Owned Start fail-fast**：注册前用 `GetRegisteredRawInputDevices` 查询键盘/鼠标当前注册归属；目标 HWND 非自身 → 抛 `RawInputRegistrationConflict`，**绝不悄悄覆盖**。真正的同进程组合必须走 broker / 注入。
- 宿主注入：InputCue 等已有 Raw Input 的宿主注入自己的 broker，模块不再注册。
- 可选 `RawInputBroker` 进程级单例：宿主与模块共用一个注册窗口分发事件。
- **注入 DTO = 归一化未分类输入**：KeyDown/KeyUp+VK+ModifierSnapshot、PointerDown/Up/Wheel+PhysicalPoint、MessageTimestamp、前台 HWND/PID 快照。分类只归 Core 状态机，防宿主/模块双重分类语义漂移。

## 后果

Phase 0 契约测试 #1 锚定冲突复现与 fail-fast 行为。

普通权限、无 `uiAccess` 的 Owned 模式不承诺观察高完整性前台应用。Phase 5 管理员记事本实测中没有产生手势候选，显式 UIA 捕获返回 `BackendUnavailable`，不能稳定细分为 `AccessDenied`。若消费者必须支持管理员窗口，需要将输入观察与 UIA 读取移入同完整性 broker，或采用满足 Windows 安装位置与签名约束的 `uiAccess` 进程；该能力不属于 v1。相关平台约束见微软的 [UIAccess 安全策略](https://learn.microsoft.com/windows/security/threat-protection/security-policy-settings/user-account-control-only-elevate-uiaccess-applications-that-are-installed-in-secure-locations) 与 [RAWINPUTDEVICE/RIDEV_INPUTSINK](https://learn.microsoft.com/windows/win32/api/winuser/ns-winuser-rawinputdevice)。

## 实现记录

- [x] Phase 0：`RawInputRegistrationGuard`（Query/EnsureOwnable）+ `RawInputRegistrationConflict` + 契约测试 #1（本机复现双注册抢占）
- [x] Phase 2：`OwnedRawInputSource`（自持线程 + 隐藏顶级窗口 + INPUTSINK 注册 + fail-fast + 纯移动过滤）+ WM_INPUT 冒烟（SendInput 合成键盘实测可达）+ 归一化 DTO（Phase 1 已落 Core）
- [ ] broker 模式：v1.5（消费者接入期）
