using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Mambo.Core.Contracts;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Dispatching;

namespace Mambo.App.ViewModels;

/// <summary>现有轨道组件的字幕编辑状态；只通过会话契约应用，快照是实际生效值的来源。</summary>
public sealed partial class SubtitleControlsViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherQueueTimer delayTimer;
    private readonly DispatcherQueueTimer styleTimer;
    private readonly CancellationTokenSource lifetime = new();
    private IPlaybackSession? session;
    private SessionSnapshot snapshot = new();
    private bool applying, disposed, delayRunning, styleRunning;
    private (double Value, long Generation, string? Track)? pendingDelay;
    private (SubtitleStyleSettings Value, long Generation)? pendingStyle;
    private long delayRevision, styleRevision;
    private (long Generation, string Font)? attemptedFontFallback;

    public SubtitleControlsViewModel()
    {
        var queue = DispatcherQueue.GetForCurrentThread();
        delayTimer = queue.CreateTimer();
        delayTimer.Interval = TimeSpan.FromMilliseconds(150);
        delayTimer.IsRepeating = false;
        delayTimer.Tick += OnDelayTimer;
        styleTimer = queue.CreateTimer();
        styleTimer.Interval = TimeSpan.FromMilliseconds(150);
        styleTimer.IsRepeating = false;
        styleTimer.Tick += OnStyleTimer;
    }

    public List<string> Fonts { get; private set; } = ["MiSans"];
    [ObservableProperty] public partial bool IsVisible { get; private set; }
    [ObservableProperty] public partial bool CanAdjustDelay { get; private set; }
    [ObservableProperty] public partial bool CanEditStyle { get; private set; }
    [ObservableProperty] public partial bool CanOverrideAss { get; private set; }
    [ObservableProperty] public partial bool CanResetStyle { get; private set; }
    [ObservableProperty] public partial string StyleNote { get; private set; } = "";
    [ObservableProperty] public partial string DelayError { get; private set; } = "";
    [ObservableProperty] public partial string StyleError { get; private set; } = "";
    [ObservableProperty] public partial string DelayText { get; set; } = "0.0";
    [ObservableProperty] public partial string FontFamily { get; set; } = "MiSans";
    [ObservableProperty] public partial string FontSizeText { get; set; } = "38";
    [ObservableProperty] public partial string TextColor { get; set; } = "#FFFFFF";
    [ObservableProperty] public partial string OutlineText { get; set; } = "1.65";
    [ObservableProperty] public partial string MarginText { get; set; } = "34";
    [ObservableProperty] public partial bool OverrideAssStyle { get; set; }

    public void Attach(IPlaybackSession value)
    {
        if (session is not null) throw new InvalidOperationException("字幕控件已经连接到播放会话。");
        session = value;
        try { Fonts = ["MiSans", .. CanvasTextFormat.GetSystemFontFamilies().Where(font => !font.Equals("MiSans", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)]; }
        catch (System.Runtime.InteropServices.COMException) { Fonts = ["MiSans"]; }
        OnPropertyChanged(nameof(Fonts));
        IsVisible = true;
        session.SnapshotChanged += OnSnapshotChanged;
        ApplySnapshot();
    }

    private void OnSnapshotChanged(object? sender, EventArgs args) => ApplySnapshot();

    private void ApplySnapshot(bool forceDelay = false, bool forceStyle = false)
    {
        if (disposed || session is null) return;
        var next = session.Snapshot;
        var changedEntry = snapshot.EntryGeneration != next.EntryGeneration;
        var changedTrack = changedEntry || snapshot.SelectedSubtitleTrackId != next.SelectedSubtitleTrackId;
        if (changedTrack)
        {
            pendingDelay = null;
            delayRevision++;
            delayTimer.Stop();
            DelayError = "";
        }
        if (changedEntry)
        {
            pendingStyle = null;
            styleRevision++;
            styleTimer.Stop();
            StyleError = "";
        }
        snapshot = next;
        var playable = next.Phase == PlayerPhase.Playing && next.EngineKind != EngineKind.External;
        CanAdjustDelay = playable && next.CanAdjustSubtitleDelay;
        CanResetStyle = playable && next.SubtitleStyleKind is SubtitleStyleKind.None or SubtitleStyleKind.Text or SubtitleStyleKind.Ass;
        CanOverrideAss = playable && next.SubtitleStyleKind == SubtitleStyleKind.Ass;
        CanEditStyle = playable && (next.SubtitleStyleKind is SubtitleStyleKind.None or SubtitleStyleKind.Text ||
            next.SubtitleStyleKind == SubtitleStyleKind.Ass && next.SubtitleStyle.OverrideAssStyle);
        StyleNote = next.SubtitleStyleKind switch
        {
            SubtitleStyleKind.None => "未选择字幕，样式将用于后续文本字幕",
            SubtitleStyleKind.Ass when !next.SubtitleStyle.OverrideAssStyle => "当前字幕使用自带样式",
            SubtitleStyleKind.Ass => "可能影响字幕特效与排版",
            SubtitleStyleKind.Bitmap => "图片字幕不支持文字样式",
            SubtitleStyleKind.Unknown => "暂无法调整此字幕的样式",
            _ => "",
        };
        applying = true;
        try
        {
            if (forceDelay || changedTrack || !delayRunning && pendingDelay is null && DelayError.Length == 0)
                DelayText = next.SubtitleDelaySeconds.ToString("0.0", CultureInfo.InvariantCulture);
            if (forceStyle || changedEntry || !styleRunning && pendingStyle is null && StyleError.Length == 0)
                ApplyStyleFields(next.SubtitleStyle);
        }
        finally { applying = false; }
        if (CanEditStyle && !styleRunning && pendingStyle is null &&
            !Fonts.Contains(next.SubtitleStyle.FontFamily, StringComparer.OrdinalIgnoreCase) &&
            attemptedFontFallback != (next.EntryGeneration, next.SubtitleStyle.FontFamily))
        {
            // 本机字体卸载后只修复字体字段；初始化字段绑定不得触发循环保存。
            attemptedFontFallback = (next.EntryGeneration, next.SubtitleStyle.FontFamily);
            styleRevision++;
            pendingStyle = (next.SubtitleStyle with { FontFamily = "MiSans" }, next.EntryGeneration);
            styleTimer.Start();
        }
    }

    private void ApplyStyleFields(SubtitleStyleSettings style)
    {
        FontFamily = Fonts.Contains(style.FontFamily, StringComparer.OrdinalIgnoreCase) ? style.FontFamily : "MiSans";
        FontSizeText = style.FontSize.ToString("0.##", CultureInfo.InvariantCulture);
        TextColor = style.TextColor;
        OutlineText = style.OutlineSize.ToString("0.##", CultureInfo.InvariantCulture);
        MarginText = style.BottomMargin.ToString("0.##", CultureInfo.InvariantCulture);
        OverrideAssStyle = style.OverrideAssStyle;
    }

    partial void OnDelayTextChanged(string value)
    {
        if (applying || disposed || session is null || !CanAdjustDelay) return;
        delayRevision++;
        pendingDelay = null;
        delayTimer.Stop();
        if (!TryNumber(value, -60, 60, out var seconds) || Math.Abs(seconds * 10 - Math.Round(seconds * 10)) > .000001)
        { DelayError = "请输入 -60.0 至 60.0 秒，精度为 0.1 秒"; return; }
        DelayError = "";
        pendingDelay = (seconds, snapshot.EntryGeneration, snapshot.SelectedSubtitleTrackId);
        delayTimer.Start();
    }

    partial void OnFontFamilyChanged(string value) => ScheduleStyle();
    partial void OnFontSizeTextChanged(string value) => ScheduleStyle();
    partial void OnTextColorChanged(string value) => ScheduleStyle();
    partial void OnOutlineTextChanged(string value) => ScheduleStyle();
    partial void OnMarginTextChanged(string value) => ScheduleStyle();
    partial void OnOverrideAssStyleChanged(bool value) => ScheduleStyle(overrideChanged: true);

    private void ScheduleStyle(bool overrideChanged = false)
    {
        if (applying || disposed || session is null || !(CanEditStyle || overrideChanged && CanOverrideAss)) return;
        styleRevision++;
        pendingStyle = null;
        styleTimer.Stop();
        if (!TryNumber(FontSizeText, 18, 72, out var size)) { StyleError = "字号范围为 18–72"; return; }
        if (!TryNumber(OutlineText, 0, 6, out var outline)) { StyleError = "描边粗细范围为 0–6"; return; }
        if (!TryNumber(MarginText, 0, 180, out var margin)) { StyleError = "底部距离范围为 0–180"; return; }
        var color = TextColor.Trim();
        if (color.Length != 7 || color[0] != '#' || !uint.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        { StyleError = "请输入 #RRGGBB 格式的文字颜色"; return; }
        var font = Fonts.Contains(FontFamily, StringComparer.OrdinalIgnoreCase) ? FontFamily : "MiSans";
        StyleError = "";
        pendingStyle = (new() { FontFamily = font, FontSize = size, TextColor = color.ToUpperInvariant(), OutlineSize = outline,
            BottomMargin = margin, OverrideAssStyle = OverrideAssStyle }, snapshot.EntryGeneration);
        styleTimer.Start();
    }

    public void AdjustDelay(double delta)
    {
        if (!CanAdjustDelay) return;
        var start = TryNumber(DelayText, -60, 60, out var draft) ? draft : snapshot.SubtitleDelaySeconds;
        var target = Math.Round(start + delta, 1);
        if (target is < -60 or > 60) { DelayError = "字幕时间范围为 -60.0 至 60.0 秒"; return; }
        DelayText = target.ToString("0.0", CultureInfo.InvariantCulture);
    }

    public void ResetDelay()
    {
        if (!CanAdjustDelay) return;
        DelayText = "0.0";
        // 即便文本已为零，也允许重试上一条失败的归零命令。
        OnDelayTextChanged(DelayText);
    }

    public void ResetStyle()
    {
        if (!CanResetStyle) return;
        applying = true;
        ApplyStyleFields(new());
        applying = false;
        StyleError = "";
        styleRevision++;
        pendingStyle = (new(), snapshot.EntryGeneration);
        styleTimer.Start();
    }

    public void FlushPending()
    {
        if (disposed) return;
        delayTimer.Stop(); styleTimer.Stop();
        _ = ApplyDelayAsync(); _ = ApplyStyleAsync();
    }

    private async void OnDelayTimer(DispatcherQueueTimer sender, object args) => await ApplyDelayAsync();
    private async void OnStyleTimer(DispatcherQueueTimer sender, object args) => await ApplyStyleAsync();

    private async Task ApplyDelayAsync()
    {
        if (delayRunning || disposed || session is null) return;
        delayRunning = true;
        try
        {
            while (!disposed && pendingDelay is { } edit)
            {
                pendingDelay = null;
                var revision = delayRevision;
                try { await session.SetSubtitleDelayAsync(edit.Value, edit.Generation, edit.Track, lifetime.Token); }
                catch (OperationCanceledException) { }
                catch (AppException error) { if (!disposed && revision == delayRevision) DelayError = error.Error.Message; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { if (!disposed && revision == delayRevision) DelayError = "字幕时间调整失败，请重试"; }
                if (!disposed && revision == delayRevision) ApplySnapshot(forceDelay: true);
            }
        }
        finally { delayRunning = false; }
    }

    private async Task ApplyStyleAsync()
    {
        if (styleRunning || disposed || session is null) return;
        styleRunning = true;
        try
        {
            while (!disposed && pendingStyle is { } edit)
            {
                pendingStyle = null;
                var revision = styleRevision;
                try { await session.SetSubtitleStyleAsync(edit.Value, edit.Generation, lifetime.Token); }
                catch (OperationCanceledException) { }
                catch (AppException error) { if (!disposed && revision == styleRevision) StyleError = error.Error.Message; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { if (!disposed && revision == styleRevision) StyleError = "设置保存失败"; }
                if (!disposed && revision == styleRevision) ApplySnapshot(forceStyle: true);
            }
        }
        finally { styleRunning = false; }
    }

    private static bool TryNumber(string text, double minimum, double maximum, out double value) =>
        double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
            CultureInfo.InvariantCulture, out value) && double.IsFinite(value) && value >= minimum && value <= maximum;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        delayTimer.Stop(); styleTimer.Stop();
        delayTimer.Tick -= OnDelayTimer; styleTimer.Tick -= OnStyleTimer;
        if (session is not null) session.SnapshotChanged -= OnSnapshotChanged;
        lifetime.Cancel(); lifetime.Dispose();
        pendingDelay = null; pendingStyle = null;
    }
}
