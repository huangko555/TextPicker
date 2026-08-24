using System.Diagnostics;
using System.Text;
using TextPicker;
using TextPicker.SmokeRunner;
using TextPicker.Windows;

Console.OutputEncoding = Encoding.UTF8;

if (args.FirstOrDefault() == "admin-helper")
{
    Environment.ExitCode = AdminInputHelper.Run();
    return;
}

var runner = new SmokeRunner();
try
{
    Environment.ExitCode = await runner.RunAsync(args.FirstOrDefault() ?? "all");
}
finally
{
    runner.Shutdown();
}

/// <summary>Phase 5 冒烟跑批器：合成真实输入（SendInput → Raw Input）驱动完整管线，逐场景判定。</summary>
internal sealed class SmokeRunner : IDisposable
{
    private readonly SelectionPicker _picker = new();
    private readonly object _gate = new();
    private readonly List<string> _events = new();
    private uint _clipboardBefore;
    private int _failedChecks;

    public async Task<int> RunAsync(string scenario)
    {
        if (scenario is "--help" or "-h" or "help")
        {
            Console.WriteLine("用法：TextPicker.SmokeRunner [all|notepad|fullscreen|ownproc|dpi|dpi-manual|chrome|edge|edge-manual|word|word-manual|admin]");
            Console.WriteLine("all 不包含需要人工操作的跨 DPI、Edge、Word 和 UAC admin 场景；多个场景可用逗号连接。");
            return 0;
        }

        _picker.SelectionCandidateReady += (_, e) => Log($"candidate gen:{e.Candidate.Generation.Value} {e.Candidate.Gesture} pid:{e.Candidate.Target.ProcessId}");
        _picker.SelectionCaptured += (_, e) => Log($"captured gen:{e.Capture.Generation?.Value} {e.Capture.Gesture} len:{e.Capture.Content.Text.Length} 锚:{e.Capture.AnchorSource} 后端:{e.Capture.Backend} 完整度:{e.Capture.Geometry.Completeness} 新鲜度:{e.Capture.Freshness?.ToString() ?? "-"} 耗时:{e.Capture.Elapsed.TotalMilliseconds:F0}ms");
        _picker.SelectionFailed += (_, e) => Log($"failed gen:{e.Generation?.Value} {e.Gesture} 原因:{e.Reason} 耗时:{e.Elapsed.TotalMilliseconds:F0}ms");
        _picker.SelectionSuperseded += (_, e) => Log($"superseded gen:{e.Generation.Value}");
        _picker.SelectionInvalidated += (_, e) => Log($"invalidated gen:{e.Generation.Value} 原因:{e.Reason}");

        _picker.Start();
        _clipboardBefore = InputSynth.ClipboardSequence;
        Log($"== picker 启动，剪贴板序列号基线 {_clipboardBefore} ==");

        var wanted = scenario == "all"
            ? new[] { "notepad", "fullscreen", "ownproc", "chrome" }
            : scenario.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var name in wanted)
        {
            Console.WriteLine($"\n########## 场景 {name} ##########");
            try
            {
                switch (name)
                {
                    case "notepad": await NotepadGestures(); break;
                    case "fullscreen": await FullScreenPause(); break;
                    case "ownproc": OwnProcessNote(); break;
                    case "dpi": await CrossDpi(); break;
                    case "dpi-manual": await CrossDpi(); break;
                    case "chrome": await Browser("chrome"); break;
                    case "edge": await Browser("edge", "manual"); break;
                    case "edge-manual": await Browser("edge", "manual"); break;
                    case "word": await Word(); break;
                    case "word-manual": await Word(); break;
                    case "admin": await AdminNotepad(); break;
                    default: Check(false, $"未知场景 {name}（使用 --help 查看可用场景）"); break;
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"!! 场景异常：{exception.GetType().Name}: {exception.Message}");
                _failedChecks++;
            }
        }

        Console.WriteLine("\n########## 汇总 ##########");
        var counters = _picker.Counters;
        Console.WriteLine($"candidates={counters.CandidatesPublished} captured={counters.CapturesSucceeded} failed={counters.CapturesFailed} superseded={counters.Superseded} invalidated={counters.Invalidated}");
        foreach (var (reason, count) in counters.FailuresByReason)
        {
            Console.WriteLine($"  失败[{reason}] x{count}");
        }

        foreach (var (reason, count) in counters.GestureDropsByReason)
        {
            Console.WriteLine($"  手势层丢弃[{reason}] x{count}");
        }

        var sequenceAfter = InputSynth.ClipboardSequence;
        Check(_clipboardBefore == sequenceAfter, $"剪贴板序列号：基线 {_clipboardBefore} → 结束 {sequenceAfter}");
        Console.WriteLine($"\n结果：{(_failedChecks == 0 ? "PASS" : $"FAIL（{_failedChecks} 项）")}");
        return _failedChecks == 0 ? 0 : 1;
    }

    public void Dispose() => Shutdown();

    public void Shutdown()
    {
        _picker.Stop();
        _picker.Dispose();
    }

    // —— 场景 ——

    private async Task NotepadGestures()
    {
        var (hwnd, pid) = LaunchWindowed("notepad.exe", "", "Notepad", 10_000);
        InputSynth.FocusWindow(hwnd);
        InputSynth.PlaceWindow(hwnd, 120, 120, 900, 500);
        Thread.Sleep(400);
        InputSynth.TypeText("The quick brown fox jumps over the lazy dog. Second line for multi select.");
        Thread.Sleep(300);

        var rect = InputSynth.WindowRect(hwnd);
        int line1 = rect.Top + 130;

        Step("框选拖选");
        await ExpectCapture(() => InputSynth.Drag(rect.Left + 80, line1, rect.Left + 520, line1 + 10));

        Step("双击选词");
        await ExpectCapture(() => InputSynth.DoubleClick(rect.Left + 250, line1));

        Step("三击选段（取代双击）");
        await ExpectCapture(() => InputSynth.TripleClick(rect.Left + 170, line1));

        Step("Ctrl+A");
        await ExpectCapture(InputSynth.CtrlA);

        Step("Shift+Right 键盘选择");
        InputSynth.KeyTap(InputSynth.VK_LEFT);
        await Task.Delay(100);
        await ExpectCapture(() => InputSynth.ShiftArrow(InputSynth.VK_RIGHT, 6));

        Step("Esc 失效（捕获后）");
        var invalidatedBefore = _picker.Counters.Invalidated;
        InputSynth.Escape();
        await Task.Delay(500);
        Check(_picker.Counters.Invalidated > invalidatedBefore, "Esc 失效");

        KillPid(pid);
    }

    private async Task FullScreenPause()
    {
        var (hwnd, pid) = LaunchWindowed("notepad.exe", "", "Notepad", 10_000);
        InputSynth.FocusWindow(hwnd);
        InputSynth.PlaceWindow(hwnd, 0, 0, 2560, 1440);    // 主显示器整面 = 全屏
        Thread.Sleep(600);
        InputSynth.TypeText("fullscreen smoke");
        Thread.Sleep(300);

        var before = Snapshot();
        InputSynth.Drag(300, 300, 800, 320);
        await Task.Delay(1200);
        var after = Snapshot();

        Check(after.Candidates == before.Candidates, $"全屏暂停：拖选未产生候选（{before.Candidates}→{after.Candidates}）");

        KillPid(pid);
    }

    private static void OwnProcessNote()
        => Console.WriteLine("自身进程过滤：由单元测试（Contract5/策略测试）+ 面板目测覆盖；控制台窗口属 conhost 进程，本跑批器无法构造可断言的同进程目标。");

    private async Task CrossDpi()
    {
        var targetPoint = InputSynth.SecondaryScreenPoint();
        if (targetPoint == null)
        {
            Console.WriteLine("跨 DPI：当前只检测到一块屏幕，跳过");
            return;
        }

        var textPath = Path.Combine(Path.GetTempPath(), $"textpicker-smoke-dpi-{Environment.ProcessId}.txt");
        File.WriteAllText(textPath, "secondary monitor dpi text");
        var (hwnd, pid) = LaunchWindowed("notepad.exe", $"\"{textPath}\"", "Notepad", 10_000);
        InputSynth.FocusWindow(hwnd);
        InputSynth.PlaceWindow(hwnd, targetPoint.Value.X, targetPoint.Value.Y, 900, 500);
        Thread.Sleep(600);

        var rect = InputSynth.WindowRect(hwnd);
        var scale = InputSynth.WindowDpiScale(hwnd);
        var ok = await ExpectManualCapture("请在副屏记事本文字上手动拖选一段", SelectionGesture.BoxSelect);
        if (ok && _picker.LastCapture is { AnchorRect: { } anchor })
        {
            var release = InputSynth.CursorPosition();
            Check(
                anchor.Left >= rect.Left && anchor.Right <= rect.Right && anchor.Top >= rect.Top && anchor.Bottom <= rect.Bottom,
                $"跨 DPI 锚点位于副屏目标窗口内：({anchor.Left:F0},{anchor.Top:F0}) {anchor.Width:F0}x{anchor.Height:F0}，窗口 DPI {scale * 100:F0}%");
            Check(
                release.X >= anchor.Left - 80 && release.X <= anchor.Right + 80
                    && release.Y >= anchor.Top - 80 && release.Y <= anchor.Bottom + 80,
                $"跨 DPI 锚点接近释放端：释放 ({release.X},{release.Y})");
        }

        KillPid(pid);
        TryDeleteFile(textPath);
    }

    private async Task Browser(string kind, string? only = null)
    {
        var htmlPath = Path.Combine(Path.GetTempPath(), "textpicker-smoke.html");
        File.WriteAllText(htmlPath, "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>body{font:18px sans-serif;margin:0}#p1{position:absolute;left:40px;top:40px}#p2{position:absolute;left:40px;top:100px}input{position:absolute;left:40px;top:180px;font-size:16px;width:420px}#editable{position:absolute;left:40px;top:260px;border:1px solid #999;padding:8px;width:520px}</style></head><body><p id=\"p1\">The quick brown fox jumps over the lazy dog again and again for the smoke test paragraph.</p><p id=\"p2\">Second paragraph with more words to drag across.</p><input value=\"standard input value text\"><div id=\"editable\" contenteditable=\"true\">contenteditable area text content</div></body></html>");

        var exe = kind == "chrome"
            ? @"C:\Program Files\Google\Chrome\Application\chrome.exe"
            : @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
        // Edge 首启会显示一次性遮挡弹窗；保留专用配置，使关闭状态可供后续复跑复用。
        var preserveProfile = kind == "edge";
        var profilePath = Path.Combine(
            Path.GetTempPath(),
            preserveProfile ? "textpicker-smoke-edge-manual" : $"textpicker-smoke-{kind}-{Environment.ProcessId}");
        Directory.CreateDirectory(profilePath);
        var browserArgs = $"--user-data-dir=\"{profilePath}\" --no-first-run --disable-default-apps --app=\"file:///{htmlPath.Replace('\\', '/')}\"";
        var (hwnd, pid) = LaunchWindowed(exe, browserArgs, kind == "chrome" ? "chrome" : "msedge", 15_000);
        InputSynth.FocusWindow(hwnd);
        InputSynth.PlaceWindow(hwnd, 120, 120, 1000, 700);
        Thread.Sleep(2000);

        var rect = InputSynth.WindowRect(hwnd);
        var client = InputSynth.ClientOrigin(hwnd);
        int y1 = client.Y + 65;

        if (only == "manual")
        {
            await ExpectManualCapture("请在第一行普通文字上手动拖选一次");
            await ExpectManualCapture("请在页面中间的标准输入框内手动拖选一次");
            await ExpectManualCapture("请在页面下方带边框的 contenteditable 区域内手动拖选一次");
        }
        else
        {
            Step("纯文本页框选");
            await ExpectCapture(() => InputSynth.Drag(client.X + 50, y1, client.X + 620, y1 + 12));

            Step("纯文本页双击");
            await ExpectCapture(() => InputSynth.DoubleClick(client.X + 190, y1));

            Step("标准输入框拖选");
            var yInput = client.Y + 192;
            await ExpectCapture(() => InputSynth.Drag(client.X + 55, yInput, client.X + 380, yInput + 8));

            Step("contenteditable 拖选");
            var yCe = client.Y + 280;
            await ExpectCapture(() => InputSynth.Drag(client.X + 55, yCe, client.X + 420, yCe + 10));
        }

        KillPid(pid, entireProcessTree: true);
        if (!preserveProfile)
        {
            TryDeleteDirectory(profilePath);
        }
    }

    private async Task<bool> ExpectManualCapture(string instruction, SelectionGesture? expectGesture = null)
    {
        var before = Snapshot();
        var failuresBefore = _picker.Counters.CapturesFailed;
        Console.WriteLine($"{instruction}（等待 45 秒）");
        for (int i = 0; i < 450; i++)
        {
            await Task.Delay(100);
            var now = Snapshot();
            if (now.Captured > before.Captured)
            {
                var actual = _picker.LastCapture?.Gesture;
                Check(expectGesture == null || actual == expectGesture,
                    expectGesture == null ? $"手动捕获 {actual}" : $"手动捕获：期望 {expectGesture}，实际 {actual}");
                return expectGesture == null || actual == expectGesture;
            }

            if (_picker.Counters.CapturesFailed > failuresBefore)
            {
                break;
            }
        }

        Check(false, $"手动捕获（最近失败：{LastFailure() ?? "45 秒内没有检测到选择手势"}）");
        return false;
    }

    private async Task Word()
    {
        var rtfPath = Path.Combine(Path.GetTempPath(), "textpicker-smoke.rtf");
        File.WriteAllText(rtfPath, @"{\rtf1\ansi The quick brown fox jumps over the lazy dog.\par Second line of the smoke test document.\par Third line for margin click.\par}");

        var (_, pid) = LaunchWindowed(@"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE", "/x \"" + rtfPath + "\"", "WINWORD", 30_000);
        Thread.Sleep(3000);
        var hwnd = WindowFinder.FindWindowForProcess(pid, 5_000);
        if (hwnd == 0)
        {
            throw new TimeoutException("Word 文档窗口未出现");
        }

        InputSynth.FocusWindow(hwnd);
        InputSynth.PlaceWindow(hwnd, 120, 120, 1000, 700);
        Thread.Sleep(500);

        await ExpectManualCapture("请在 Word 正文中手动拖选一段文字", SelectionGesture.BoxSelect);
        await ExpectManualCapture("请先单击第二或第三行正文，再按 Ctrl+Shift+Home", SelectionGesture.ShiftKeyboard);
        await ExpectManualCapture("请单击第三行左侧页边距，使 Word 选中整行", SelectionGesture.ClickSelection);

        KillPid(pid);
    }

    private async Task AdminNotepad()
    {
        Console.WriteLine("!! 即将弹出一次 UAC：提权助手会启动并操作管理员记事本——请点「是」");
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位 SmokeRunner 可执行文件");
        var before = Snapshot();
        using var helper = Process.Start(new ProcessStartInfo(executable, "admin-helper")
        {
            UseShellExecute = true,
            Verb = "runas",
        }) ?? throw new InvalidOperationException("无法启动提权输入助手");
        await helper.WaitForExitAsync();
        await Task.Delay(750);
        var after = Snapshot();
        Check(helper.ExitCode == 0, $"管理员输入助手退出码 {helper.ExitCode}");

        var counters = _picker.Counters;
        bool accessDenied = counters.FailuresByReason.TryGetValue(CaptureFailureReason.AccessDenied, out var denied) && denied > 0;
        Check(accessDenied, $"管理员目标：AccessDenied 可诊断（x{denied}；候选 {before.Candidates}→{after.Candidates}；失败明细：{string.Join(",", counters.FailuresByReason)}）");
    }

    // —— 基础设施 ——

    private static (nint Hwnd, int Pid) LaunchWindowed(string exe, string args, string processName, int timeoutMs)
    {
        var existing = WindowFinder.SnapshotWindows(processName);
        _ = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true }) ?? throw new InvalidOperationException($"无法启动 {exe}");
        var (hwnd, pid) = WindowFinder.FindNewWindow(existing, processName, timeoutMs);
        if (hwnd == 0)
        {
            throw new TimeoutException($"等待 {processName} 新窗口超时");
        }

        return (hwnd, pid);
    }

    private static void KillPid(int pid, bool entireProcessTree = false)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void Step(string name) => Console.WriteLine($"\n-- {name}");

    private (long Candidates, long Captured) Snapshot()
    {
        var counters = _picker.Counters;
        return (counters.CandidatesPublished, countsCaptured());
        long countsCaptured() => counters.CapturesSucceeded;
    }

    private async Task<bool> ExpectCapture(Action trigger, SelectionGesture? expectGesture = null)
    {
        var before = Snapshot();
        trigger();
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(100);
            var now = Snapshot();
            if (now.Captured > before.Captured)
            {
                var last = _picker.LastCapture;
                if (expectGesture != null && last?.Gesture != expectGesture)
                {
                    Check(false, $"手势匹配：期望 {expectGesture} 实得 {last?.Gesture}");
                    return false;
                }

                Check(true, $"捕获 {last?.Gesture}");
                return true;
            }

            var countersNow = _picker.Counters;
            if (countersNow.CapturesFailed + countersNow.CapturesSucceeded > before.Candidates)
            {
                break;    // 已有终止（失败）
            }
        }

        Check(false, $"捕获（candidates {before.Candidates}→{_picker.Counters.CandidatesPublished}，failed={_picker.Counters.CapturesFailed}，最近失败：{LastFailure() ?? "无"}）");
        return false;
    }

    private void Check(bool passed, string description)
    {
        Console.WriteLine($"{description} {(passed ? "✅" : "❌")}");
        if (!passed)
        {
            _failedChecks++;
        }
    }

    private string? LastFailure()
    {
        lock (_gate)
        {
            return _events.LastOrDefault(line => line.StartsWith("failed", StringComparison.Ordinal));
        }
    }

    private void Log(string line)
    {
        lock (_gate)
        {
            _events.Add(line);
        }

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {line}");
    }
}

/// <summary>以管理员权限运行，仅负责制造真实高完整性输入；Picker 始终留在普通权限父进程。</summary>
internal static class AdminInputHelper
{
    public static int Run()
    {
        int pid = 0;
        try
        {
            var existing = WindowFinder.SnapshotWindows("Notepad");
            _ = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true })
                ?? throw new InvalidOperationException("无法启动管理员记事本");
            var (hwnd, targetPid) = WindowFinder.FindNewWindow(existing, "Notepad", 20_000);
            pid = targetPid;
            if (hwnd == 0)
            {
                throw new TimeoutException("未找到管理员记事本窗口");
            }

            InputSynth.FocusWindow(hwnd);
            InputSynth.PlaceWindow(hwnd, 120, 120, 900, 500);
            Thread.Sleep(500);
            InputSynth.TypeText("elevated notepad smoke text");
            Thread.Sleep(300);
            var rect = InputSynth.WindowRect(hwnd);
            InputSynth.Drag(rect.Left + 80, rect.Top + 130, rect.Left + 490, rect.Top + 145);
            Thread.Sleep(1500);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"管理员输入助手失败：{exception.GetType().Name}: {exception.Message}");
            return 1;
        }
        finally
        {
            if (pid != 0)
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    process.Kill();
                }
                catch (ArgumentException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
    }
}
