using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TextPicker.Windows.Tests.Phase3;

/// <summary>
/// 进程内 WPF 宿主（InputCue ProbeDiagnosticRunner 模式）：单一 STA 引擎线程 + 每测试一个 Topmost 窗口。
/// Application 每 AppDomain 仅一个（WPF 限制）：引擎懒启动后常驻，窗口按需创建/关闭。
/// 测试从 xunit 线程经 Dispatcher 驱动 UI（绝不阻塞 WPF UI 线程——阻塞会掩盖为 BackendTimeout）。
/// 宿主必须在 [Collection("WpfSerial")] 下使用（避免并行窗口互抢焦点）。
/// </summary>
public sealed class WpfHost : IDisposable
{
    private Window? _window;
    private TextBox? _textBox;

    public const string TextBoxContent = "The quick brown fox jumps over the lazy dog";

    public static int ProcessId => Environment.ProcessId;

    public WpfHost()
    {
        WpfEngine.EnsureStarted();
        (_window, _textBox) = WpfEngine.Invoke(() =>
        {
            var window = new Window
            {
                Left = 80,
                Top = 80,
                Width = 460,
                Height = 220,
                Topmost = true,
                ShowActivated = true,
                Title = "TextPicker test host",
            };
            var textBox = new TextBox { Text = TextBoxContent, FontSize = 16 };
            window.Content = textBox;
            window.Show();
            textBox.Focus();
            return (window, textBox);
        });
        WpfEngine.PumpUntilIdle();
    }

    public nint WindowHandle => Invoke(() => _window is { } window ? new System.Windows.Interop.WindowInteropHelper(window).Handle : 0);

    public static T Invoke<T>(Func<T> action) => WpfEngine.Invoke(action);

    public void Select(int start, int length)
    {
        _ = Invoke(() =>
        {
            _textBox!.Focus();
            _textBox.Select(start, length);
            return 0;
        });
        WpfEngine.PumpUntilIdle();
    }

    public void Activate()
    {
        _ = Invoke(() =>
        {
            _window!.Activate();
            _textBox!.Focus();
            return 0;
        });
        WpfEngine.PumpUntilIdle();
    }

    public PhysicalScreenPoint PointInTextBox(double relativeX = 50, double relativeY = 8)
        => Invoke(() =>
        {
            var point = _textBox!.PointToScreen(new Point(relativeX, relativeY));
            return new PhysicalScreenPoint(point.X, point.Y);
        });

    public void Dispose()
    {
        var window = _window;
        if (window != null)
        {
            _ = WpfEngine.Invoke<object?>(() =>
            {
                window.Close();
                return null;
            });
            WpfEngine.PumpUntilIdle();
        }

        _window = null;
        _textBox = null;
    }

    private static class WpfEngine
    {
        private static readonly Thread Thread = new(Run)
        {
            IsBackground = true,
            Name = "TextPicker.WpfEngine",
        };

        private static readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static Application? _application;

        public static void EnsureStarted()
        {
            lock (Thread)
            {
                if (!Thread.IsAlive)
                {
                    Thread.SetApartmentState(ApartmentState.STA);
                    Thread.Start();
                    if (!Ready.Task.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("WPF engine failed to start");
                    }
                }
            }
        }

        public static T Invoke<T>(Func<T> action)
        {
            EnsureStarted();
            return _application!.Dispatcher.Invoke(action);
        }

        public static void PumpUntilIdle()
        {
            _ = Invoke<object?>(() =>
            {
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
                return null;
            });
            Thread.Sleep(50);
        }

        private static void Run()
        {
            _application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Ready.SetResult();
            _application.Run();
        }
    }
}
