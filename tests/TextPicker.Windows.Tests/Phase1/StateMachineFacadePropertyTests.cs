using TextPicker;
using TextPicker.Windows;
using TextPicker.Windows.Tests.Phase0;

namespace TextPicker.Windows.Tests.Phase1;

/// <summary>property-based 全路径：随机合成输入流 → 真实状态机 + 门面 → 5 号不变式
/// （CandidateReady==1、terminal 互斥唯一、Captured 后 Invalidated≤1、generation 无洞）。</summary>
public sealed class StateMachineFacadePropertyTests
{
    private static ForegroundTargetSnapshot Fg => new(0x1234, 4321);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task RandomInputStreams_PreserveLifecycleInvariants(int seed)
    {
        var feed = new CoreGestureFeed();
        var backend = new FakeBackend((_, _) => Task.FromResult(FakeBackend.Ok()));
        using var picker = new SelectionPicker(feed, backend);
        using var log = new PickerEventLog();
        log.Attach(picker);
        picker.Start();

        var random = new Random(seed);
        long t = 1000;
        var clickAnchor = new PhysicalScreenPoint(500, 500);

        for (int i = 0; i < 150; i++)
        {
            t += random.Next(0, 350);
            int kind = random.Next(0, 10);
            switch (kind)
            {
                case 0:
                case 1:
                case 2:    // 拖选（位移 0..30px 随机跨越阈值）
                    InjectPointerPair(feed, t, clickAnchor, random.Next(0, 31), random.Next(0, 31), shift: random.Next(0, 4) == 0);
                    break;
                case 3:    // 连击（同点小抖动）
                    InjectPointerPair(feed, t, clickAnchor, random.Next(0, 4), random.Next(0, 4), shift: false);
                    break;
                case 4:    // 滚轮打断
                    feed.Inject(new InputRecord.PointerWheel(t, Fg, clickAnchor, ModifierSnapshot.None, -120));
                    break;
                case 5:    // 右/中键打断
                    var button = random.Next(0, 2) == 0 ? PointerButton.Right : PointerButton.Middle;
                    feed.Inject(new InputRecord.PointerDown(t, Fg, button, clickAnchor, ModifierSnapshot.None));
                    break;
                case 6:    // 键盘：导航/字符，Shift/Ctrl 随机，Down/Up 随机
                {
                    var vk = (ushort)random.Next(0x20, 0x30);
                    var action = random.Next(0, 2) == 0 ? InputKeyAction.Down : InputKeyAction.Up;
                    var modifiers = new ModifierSnapshot(
                        Ctrl: random.Next(0, 3) == 0,
                        Shift: random.Next(0, 2) == 0,
                        Alt: false,
                        Win: false);
                    feed.Inject(new InputRecord.Key(t, Fg, action, vk, modifiers));
                    break;
                }

                case 7:    // Esc 打断
                    feed.Inject(new InputRecord.Key(t, Fg, InputKeyAction.Down, 0x1B, ModifierSnapshot.None));
                    break;
                default:   // 单击（无 Shift）
                    InjectPointerPair(feed, t, clickAnchor, random.Next(0, 4), random.Next(0, 4), shift: false);
                    break;
            }
        }

        // 排空：所有候选都拿到恰好一个终止事件（无悬挂 generation）。
        bool Drained() => log.SnapshotOf().Candidates.Count
            == log.SnapshotOf().Captured.Count + log.SnapshotOf().Failed.Count + log.SnapshotOf().Superseded.Count;
        Assert.True(await PickerEventLog.EventuallyAsync(Drained, timeoutMs: 10_000), $"seed={seed}: 有候选未终结");

        log.AssertLifecycleInvariants();
    }

    private static void InjectPointerPair(CoreGestureFeed feed, long t, PhysicalScreenPoint anchor, int dx, int dy, bool shift)
    {
        var modifiers = new ModifierSnapshot(false, shift, false, false);
        feed.Inject(new InputRecord.PointerDown(t, Fg, PointerButton.Left, anchor, modifiers));
        feed.Inject(new InputRecord.PointerUp(t + randomGap(t), Fg, PointerButton.Left, new PhysicalScreenPoint(anchor.X + dx, anchor.Y + dy), modifiers));
    }

    private static long randomGap(long t) => Math.Abs(t.GetHashCode() % 40) + 10;    // 10..50ms 稳定小间隔
}
