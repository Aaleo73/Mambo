namespace Mambo.App.BulletChat;

[Flags]
internal enum BulletChatClockChange
{
    None = 0,
    /// <summary>暂停与继续之间切换；时间没有跳变。</summary>
    Running = 1,
    /// <summary>倍速变化；时间没有跳变。</summary>
    Rate = 2,
    /// <summary>跳转，或与播放器的偏差超出容差：需要按新时间重建画面。</summary>
    Jump = 4,
}

/// <summary>
/// 合成器时钟在界面线程一侧的镜像：对它做的每个操作（停、走、变速、重设）都与合成器上的同步进行，
/// 所以它的读数就是弹幕此刻所处的时间。再拿它与播放器快照比较，偏差过大才重新对时。不依赖 WinUI。
/// </summary>
internal sealed class BulletChatClock
{
    /// <summary>与播放器的偏差在此之内不纠正：0.3 秒的早晚看不出来，而纠正会让所有弹幕跳一下。</summary>
    public const double Tolerance = 0.3;
    private double anchorSeconds;
    private long anchorMilliseconds;
    private bool started;

    public bool Running { get; private set; }
    public double Rate { get; private set; } = 1;

    /// <summary>此刻弹幕所处的播放时间（秒）。</summary>
    public double Now(long nowMilliseconds) =>
        Running ? anchorSeconds + (nowMilliseconds - anchorMilliseconds) / 1000.0 * Rate : anchorSeconds;

    /// <param name="positionSeconds">快照里的播放位置。</param>
    /// <param name="sampleMilliseconds">该位置对应的时刻（快照发布时间），不晚于 nowMilliseconds。</param>
    public BulletChatClockChange Observe(double positionSeconds, double rate, bool running, long sampleMilliseconds, long nowMilliseconds)
    {
        if (!double.IsFinite(rate) || rate <= 0) rate = 1;
        if (!double.IsFinite(positionSeconds) || positionSeconds < 0) positionSeconds = 0;
        sampleMilliseconds = Math.Min(sampleMilliseconds, nowMilliseconds);
        if (!started || Math.Abs(positionSeconds - Now(sampleMilliseconds)) > Tolerance)
        {
            started = true;
            // 把采样位置外推到现在；之后的读数从这里起算。
            anchorSeconds = positionSeconds + (running ? (nowMilliseconds - sampleMilliseconds) / 1000.0 * rate : 0);
            anchorMilliseconds = nowMilliseconds;
            Running = running;
            Rate = rate;
            return BulletChatClockChange.Jump;
        }
        var change = BulletChatClockChange.None;
        if (running != Running) change |= BulletChatClockChange.Running;
        if (Math.Abs(rate - Rate) > 1e-6) change |= BulletChatClockChange.Rate;
        if (change == BulletChatClockChange.None) return change;
        // 没有跳变：从镜像自己的读数接着走，与合成器"从当前值继续"一致。
        anchorSeconds = Now(nowMilliseconds);
        anchorMilliseconds = nowMilliseconds;
        Running = running;
        Rate = rate;
        return change;
    }

    /// <summary>忘掉当前时间；下一次 Observe 必定报告 Jump。</summary>
    public void Reset()
    {
        started = false;
        Running = false;
        anchorSeconds = 0;
    }
}
