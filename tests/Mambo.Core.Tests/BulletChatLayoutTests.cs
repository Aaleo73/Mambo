using Mambo.App.BulletChat;
using Xunit;

namespace Mambo.Core.Tests;

/// <summary>弹幕层里不依赖 WinUI 的两块逻辑：镜像时钟与分轨。</summary>
public sealed class BulletChatLayoutTests
{
    // ---- 时钟 ----

    [Fact]
    public void FirstObservationAnchorsAndExtrapolatesAtPlaybackRate()
    {
        var clock = new BulletChatClock();
        Assert.Equal(BulletChatClockChange.Jump, clock.Observe(100, 1.5, running: true, sampleMilliseconds: 1000, nowMilliseconds: 1000));
        Assert.Equal(100, clock.Now(1000), 6);
        Assert.Equal(103, clock.Now(3000), 6);
        Assert.True(clock.Running);
        Assert.Equal(1.5, clock.Rate);
    }

    [Fact]
    public void PublicationDelayIsAccountedForWhenAnchoring()
    {
        var clock = new BulletChatClock();
        // 位置是 200ms 前的采样；到现在播放器已经又走了 0.2 秒。
        clock.Observe(50, 1, running: true, sampleMilliseconds: 800, nowMilliseconds: 1000);
        Assert.Equal(50.2, clock.Now(1000), 6);
        // 暂停时不外推。
        var paused = new BulletChatClock();
        paused.Observe(50, 1, running: false, sampleMilliseconds: 800, nowMilliseconds: 1000);
        Assert.Equal(50, paused.Now(5000), 6);
    }

    [Fact]
    public void SmallDriftIsIgnoredAndLargeDriftReanchors()
    {
        var clock = new BulletChatClock();
        clock.Observe(10, 1, true, 0, 0);
        // 一秒后播放器报告 11.2：偏差 0.2 秒，在容差内，不动。
        Assert.Equal(BulletChatClockChange.None, clock.Observe(11.2, 1, true, 1000, 1000));
        Assert.Equal(11, clock.Now(1000), 6);
        // 报告 11.5 + ：偏差超过容差，按播放器的时间重来。
        Assert.Equal(BulletChatClockChange.Jump, clock.Observe(12.5, 1, true, 2000, 2000));
        Assert.Equal(12.5, clock.Now(2000), 6);
        // 向后跳转同理。
        Assert.Equal(BulletChatClockChange.Jump, clock.Observe(3, 1, true, 2100, 2100));
        Assert.Equal(3, clock.Now(2100), 6);
    }

    [Fact]
    public void PauseAndResumeContinueFromMirrorWithoutJump()
    {
        var clock = new BulletChatClock();
        clock.Observe(20, 1, true, 0, 0);
        // 播放器在 20.9 处暂停，但通知晚到：镜像已走到 21.0。它停在自己的读数上，不回跳。
        Assert.Equal(BulletChatClockChange.Running, clock.Observe(20.9, 1, false, 1000, 1000));
        Assert.False(clock.Running);
        Assert.Equal(21, clock.Now(1000), 6);
        Assert.Equal(21, clock.Now(9000), 6);
        Assert.Equal(BulletChatClockChange.None, clock.Observe(20.9, 1, false, 9000, 9000));
        Assert.Equal(BulletChatClockChange.Running, clock.Observe(20.9, 1, true, 9500, 9500));
        Assert.Equal(21.5, clock.Now(10000), 6);
    }

    [Fact]
    public void SeekWhilePausedIsAJump()
    {
        var clock = new BulletChatClock();
        clock.Observe(20, 1, false, 0, 0);
        Assert.Equal(BulletChatClockChange.Jump, clock.Observe(300, 1, false, 500, 500));
        Assert.Equal(300, clock.Now(99999), 6);
    }

    [Fact]
    public void RateChangeKeepsPositionAndChangesPace()
    {
        var clock = new BulletChatClock();
        clock.Observe(0, 1, true, 0, 0);
        Assert.Equal(BulletChatClockChange.Rate, clock.Observe(1, 2, true, 1000, 1000));
        Assert.Equal(1, clock.Now(1000), 6);
        Assert.Equal(3, clock.Now(2000), 6);
        // 同时暂停并变速，两个标志都报告。
        Assert.Equal(BulletChatClockChange.Running | BulletChatClockChange.Rate, clock.Observe(3, 0.5, false, 2000, 2000));
    }

    [Fact]
    public void InvalidInputsAreSanitisedAndResetForcesJump()
    {
        var clock = new BulletChatClock();
        clock.Observe(double.NaN, 0, true, 5000, 1000);
        Assert.Equal(1, clock.Rate);
        Assert.Equal(0, clock.Now(1000), 6);
        clock.Observe(1, double.PositiveInfinity, true, 2000, 2000);
        Assert.Equal(1, clock.Rate);
        clock.Reset();
        Assert.False(clock.Running);
        Assert.Equal(BulletChatClockChange.Jump, clock.Observe(2, 1, true, 3000, 3000));
    }

    // ---- 分轨 ----

    private const double Viewport = 1280;
    private const double Duration = 15;

    [Fact]
    public void ScrollUsesTopmostLaneAndDropsWhenAllAreBusy()
    {
        var planner = Planner(scroll: 3);
        Assert.Equal(0, planner.PlaceScroll(0, 200, Viewport, Duration));
        Assert.Equal(1, planner.PlaceScroll(0, 200, Viewport, Duration));
        Assert.Equal(2, planner.PlaceScroll(0, 200, Viewport, Duration));
        Assert.Equal(-1, planner.PlaceScroll(0, 200, Viewport, Duration));
        // 前一条的尾部进入画面并留出间隔后，最上面的轨道重新可用。
        var entered = (200 + BulletChatLanePlanner.Gap) / ((Viewport + 200) / Duration);
        Assert.Equal(-1, planner.PlaceScroll(entered - 0.01, 200, Viewport, Duration));
        Assert.Equal(0, planner.PlaceScroll(entered + 0.01, 200, Viewport, Duration));
    }

    [Fact]
    public void FasterCommentMayNotCatchUpWithSlowerOneAhead()
    {
        var planner = Planner(scroll: 2);
        Assert.Equal(0, planner.PlaceScroll(0, 100, Viewport, Duration));
        // 一条很长（因此更快）的弹幕紧随其后：在同一轨道里会追上前一条，只能去下一轨。
        Assert.Equal(1, planner.PlaceScroll(3, 1200, Viewport, Duration));
        // 等到它到达左缘时前一条已经完全离开，才可以同轨。
        planner.Reset();
        Assert.Equal(0, planner.PlaceScroll(0, 100, Viewport, Duration));
        var safe = Duration - Viewport / ((Viewport + 1200) / Duration);
        Assert.Equal(1, planner.PlaceScroll(safe - 0.05, 1200, Viewport, Duration));
        planner.Reset();
        Assert.Equal(0, planner.PlaceScroll(0, 100, Viewport, Duration));
        Assert.Equal(0, planner.PlaceScroll(safe + 0.05, 1200, Viewport, Duration));
    }

    [Fact]
    public void CommentsSharingALaneNeverOverlap()
    {
        var random = new Random(20261005);
        var planner = Planner(scroll: 12);
        var lanes = Enumerable.Range(0, planner.ScrollLanes).Select(_ => new List<(double Time, double Width)>()).ToArray();
        var time = 0.0;
        var placed = 0;
        for (var index = 0; index < 4000; index++)
        {
            time += random.NextDouble() * 0.25;
            var width = 40 + random.NextDouble() * 900;
            var lane = planner.PlaceScroll(time, width, Viewport, Duration);
            if (lane < 0) continue;
            lanes[lane].Add((time, width));
            placed++;
        }
        Assert.True(placed > 500, "高密度下仍应放下相当数量的弹幕");
        foreach (var lane in lanes)
            for (var index = 1; index < lane.Count; index++)
            {
                var (aheadTime, aheadWidth) = lane[index - 1];
                var (behindTime, behindWidth) = lane[index];
                // 两条都是匀速直线运动：只要在后一条出现时和前一条离开时都不重叠，中间就不会重叠。
                foreach (var moment in new[] { behindTime, aheadTime + Duration })
                {
                    var aheadTail = Viewport - (Viewport + aheadWidth) / Duration * (moment - aheadTime) + aheadWidth;
                    var behindHead = Viewport - (Viewport + behindWidth) / Duration * (moment - behindTime);
                    Assert.True(behindHead >= aheadTail - 1e-6, $"同轨重叠：{aheadTime:F2}/{aheadWidth:F0} 与 {behindTime:F2}/{behindWidth:F0}");
                }
            }
    }

    [Fact]
    public void ReplayingTheSameCommentsGivesTheSameLanes()
    {
        var random = new Random(7);
        var comments = new List<(double Time, double Width)>();
        var time = 0.0;
        for (var index = 0; index < 300; index++) comments.Add((time += random.NextDouble() * 0.3, 60 + random.NextDouble() * 500));
        var planner = Planner(scroll: 8);
        var first = comments.Select(comment => planner.PlaceScroll(comment.Time, comment.Width, Viewport, Duration)).ToArray();
        planner.Reset();
        var second = comments.Select(comment => planner.PlaceScroll(comment.Time, comment.Width, Viewport, Duration)).ToArray();
        Assert.Equal(first, second);
        // 重放时遇到比轨道里已有弹幕更早的时间，不往那条轨道里插。
        Assert.NotEqual(0, planner.PlaceScroll(0, 100, Viewport, Duration));
    }

    [Fact]
    public void FixedCommentsStackThenFreeTheirLaneAfterTheStay()
    {
        var planner = Planner(scroll: 4, top: 2, bottom: 1);
        Assert.Equal(0, planner.PlaceTop(10, 5));
        Assert.Equal(1, planner.PlaceTop(11, 5));
        Assert.Equal(-1, planner.PlaceTop(12, 5));
        Assert.Equal(0, planner.PlaceTop(15, 5));
        // 顶部与底部互不占用。
        Assert.Equal(0, planner.PlaceBottom(10, 5));
        Assert.Equal(-1, planner.PlaceBottom(14.9, 5));
        Assert.Equal(0, planner.PlaceBottom(15, 5));
        planner.Reset();
        Assert.Equal(0, planner.PlaceTop(0, 5));
        Assert.Equal(0, planner.PlaceBottom(0, 5));
    }

    [Fact]
    public void ReconfiguringKeepsOccupancyAndRejectsDegenerateInput()
    {
        var planner = Planner(scroll: 2, top: 1, bottom: 1);
        Assert.Equal(0, planner.PlaceScroll(0, 200, Viewport, Duration));
        Assert.Equal(0, planner.PlaceTop(0, 5));
        planner.Configure(4, 2, 2);
        Assert.Equal(4, planner.ScrollLanes);
        // 第 0 轨仍被占着，新弹幕去第 1 轨；新增的顶部轨道是空的。
        Assert.Equal(1, planner.PlaceScroll(0, 200, Viewport, Duration));
        Assert.Equal(1, planner.PlaceTop(1, 5));
        planner.Configure(0, 0, 0);
        Assert.Equal(-1, planner.PlaceScroll(100, 200, Viewport, Duration));
        Assert.Equal(-1, planner.PlaceTop(100, 5));
        planner.Configure(2, 1, 1);
        Assert.Equal(-1, planner.PlaceScroll(100, 200, 0, Duration));
        Assert.Equal(-1, planner.PlaceScroll(100, 200, Viewport, 0));
        Assert.Equal(-1, planner.PlaceBottom(100, 0));
    }

    private static BulletChatLanePlanner Planner(int scroll, int top = 1, int bottom = 1)
    {
        var planner = new BulletChatLanePlanner();
        planner.Configure(scroll, top, bottom);
        return planner;
    }
}
