using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mambo.Core.Contracts;

namespace Mambo.Player.LibMpv;

/// <summary>拥有完整画质预设；渲染确认独立于会话 actor，不消费其事件队列。</summary>
internal sealed class VideoQualityController : IAsyncDisposable
{
    private readonly MpvCore core;
    // 默认只使用经过嵌入哈希校验的安装资源；测试可注入坏着色器验证真实 GPU 回滚。
    private readonly Func<VideoQualityMode, string[]> resolveResources;
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private VideoQualityMode confirmedMode;
    // 当前完整写入 mpv 的预设；null 表示尚未写入或只写了一半。
    private VideoQualityMode? written;
    // 写入那一刻的渲染统计与失败计数：之后才出现的帧和失败才属于这套预设。
    private Dictionary<string, RenderPass[]> writtenBefore = new(StringComparer.Ordinal);
    private long writtenFailures;
    private bool sharedWritten;
    private int recovering;

    /// <summary>已确认的增强在之后的渲染中失效，增强已清除；在后台线程触发。</summary>
    public event Action? Lost;

    public VideoQualityController(MpvCore core, Func<VideoQualityMode, string[]>? resourceResolver = null)
    {
        this.core = core;
        resolveResources = resourceResolver ?? ShaderPaths;
        core.ShaderFailed += OnShaderFailed;
    }

    public async Task PrepareAsync(VideoQualityMode mode, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        token = linked.Token;
        ValidateMode(mode);
        await writer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var paths = resolveResources(mode);
            await SetPresetAsync(mode, paths, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not (OperationCanceledException or AppException))
        {
            throw Failure("画质资源无法加载，请修复安装后重试。");
        }
        finally { writer.Release(); }
    }

    public async Task ApplyAsync(VideoQualityMode mode, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        token = linked.Token;
        ValidateMode(mode);
        await writer.WaitAsync(token).ConfigureAwait(false);
        var previous = confirmedMode;
        try
        {
            // 校验失败发生在修改属性前；期望哈希来自程序集内嵌的原始发布资源。
            var paths = resolveResources(mode);
            await SetPresetAsync(mode, paths, token).ConfigureAwait(false);
            // 没有画面的条目没有可确认的帧。
            if (mode != VideoQualityMode.Standard && core.GetProperty("vid") is not MpvValue.Text { Value: "no" })
                await ConfirmRenderingAsync(mode, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            confirmedMode = mode;
        }
        catch (Exception error)
        {
            // 即使请求取消也先撤销半套预设，再让新请求获得 writer。
            using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try
            {
                await SetPresetAsync(previous, resolveResources(previous), rollback.Token).ConfigureAwait(false);
                // 切集/关闭取消时可能已经没有旧帧；新条目的 Apply 会重新确认。
                if (error is not OperationCanceledException && previous != VideoQualityMode.Standard &&
                    !lifetime.IsCancellationRequested && core.GetProperty("video-params") is MpvValue.Map)
                    await ConfirmRenderingAsync(previous, rollback.Token).ConfigureAwait(false);
            }
            catch
            {
                // 上一套资源也损坏时只清除增强，不重新加载正在播放的媒体。
                try
                {
                    using var fallback = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    await SetPresetAsync(VideoQualityMode.Standard, [], fallback.Token).ConfigureAwait(false);
                    confirmedMode = VideoQualityMode.Standard;
                }
                catch { /* 引擎可能已关闭，不能用清理错误覆盖原始取消或失败。 */ }
            }
            if (previous != VideoQualityMode.Standard && confirmedMode == VideoQualityMode.Standard)
                throw new AppException(new(AppErrorKind.Player, "player.video_quality_reset_to_standard", "画质效果无法恢复，已切回标准。", false));
            if (error is OperationCanceledException) throw;
            throw Failure("画质模式未能启用，已恢复可用画质。");
        }
        finally { writer.Release(); }
    }

    private async Task SetPresetAsync(VideoQualityMode mode, string[] paths, CancellationToken token)
    {
        // 已经完整写入的预设不重写：加载前准备好的、换集沿用的模式，首帧就带着效果。
        if (written == mode) return;
        written = null;
        if (!sharedWritten)
        {
            // 三种模式相同的值只写一次。SCALER_INHERIT 的公开选项值是空字符串，不是字面量 "inherit"。
            await SetPropertyAsync("glsl-shader-opts", new MpvValue.Map(new Dictionary<string, MpvValue?>()), token).ConfigureAwait(false);
            await SetPropertyAsync("dscale", new MpvValue.Text("hermite"), token).ConfigureAwait(false);
            await SetPropertyAsync("cscale", new MpvValue.Text(""), token).ConfigureAwait(false);
            sharedWritten = true;
        }
        await SetPropertyAsync("scale", new MpvValue.Text(mode == VideoQualityMode.Standard ? "lanczos" : "ewa_lanczossharp"), token).ConfigureAwait(false);
        await SetPropertyAsync("scale-antiring", new MpvValue.Number(mode == VideoQualityMode.Standard ? 0 : .6), token).ConfigureAwait(false);
        writtenBefore = Passes(core.GetProperty("vo-passes"));
        writtenFailures = core.ShaderFailureVersion;
        // 整个列表一次换掉，不经过空列表：切换途中不会漏出一帧没有效果的画面。
        await SetPropertyAsync("glsl-shaders", new MpvValue.Array(paths.Select(path => (MpvValue?)new MpvValue.Text(path)).ToArray()), token).ConfigureAwait(false);
        written = mode;
    }

    private async Task SetPropertyAsync(string name, MpvValue value, CancellationToken token)
    {
        try { await core.SetPropertyAsync(name, value, token).ConfigureAwait(false); }
        catch (InvalidOperationException)
        {
            throw new AppException(new(AppErrorKind.Player, "player.video_quality.parameter." + name,
                "画质参数设置失败（" + name + "）。", false));
        }
    }

    private async Task ConfirmRenderingAsync(VideoQualityMode mode, CancellationToken token)
    {
        var (expected, other) = mode == VideoQualityMode.Clear
            ? ("Mambo Clear", "Mambo Anime")
            : ("Mambo Anime restore HDR source and bounded detail gain", "Mambo Clear");
        // 画面还没开始渲染（起播慢、缓冲）不算失败：期限从写入之后的第一帧算起。
        Stopwatch? rendering = null;
        while (rendering is null || rendering.Elapsed < TimeSpan.FromSeconds(20))
        {
            token.ThrowIfCancellationRequested();
            if (core.ShaderFailureVersion != writtenFailures) throw Failure("画质着色器编译失败。");
            foreach (var (kind, passes) in Passes(core.GetProperty("vo-passes")))
            {
                // fresh/redraw 会分别保留上一帧的统计，只确认写入之后改变的一组。
                if (passes.Length == 0 || writtenBefore.TryGetValue(kind, out var old) && old.SequenceEqual(passes)) continue;
                rendering ??= Stopwatch.StartNew();
                if (passes.Any(pass => pass.Description.Contains(expected, StringComparison.Ordinal)) &&
                    !passes.Any(pass => pass.Description.Contains(other, StringComparison.Ordinal)))
                {
                    await Task.Delay(60, token).ConfigureAwait(false);
                    if (core.ShaderFailureVersion != writtenFailures) throw Failure("画质着色器编译失败。");
                    return;
                }
            }
            await Task.Delay(40, token).ConfigureAwait(false);
        }
        throw Failure("未能确认画质效果已渲染，请稍后重试。");
    }

    // 事件线程上只排队；同一次失败的多行日志合并为一次检查。
    private void OnShaderFailed()
    {
        if (Interlocked.Exchange(ref recovering, 1) == 0) _ = Task.Run(RecoverAsync);
    }

    private async Task RecoverAsync()
    {
        var lost = false;
        try
        {
            await writer.WaitAsync(lifetime.Token).ConfigureAwait(false);
            try
            {
                Volatile.Write(ref recovering, 0);
                // Apply 持有 writer 时出现的失败由它自己回滚；这里只处理确认之后才失效的增强，
                // 例如窗口放大后才第一次编译的超分 pass。
                if (confirmedMode == VideoQualityMode.Standard || written != confirmedMode ||
                    core.ShaderFailureVersion == writtenFailures) return;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                await SetPresetAsync(VideoQualityMode.Standard, [], timeout.Token).ConfigureAwait(false);
                confirmedMode = VideoQualityMode.Standard;
                lost = true;
            }
            finally { writer.Release(); }
        }
        catch { /* 引擎可能已关闭；下一次 Apply 会重新写入并确认。 */ }
        if (lost) Lost?.Invoke();
    }

    private static string[] ShaderPaths(VideoQualityMode mode)
    {
        if (mode == VideoQualityMode.Standard) return [];
        var name = mode == VideoQualityMode.Clear ? "Mambo_Clear.glsl" : "Mambo_Anime.glsl";
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "mpv", "shaders", name));
        using var expected = typeof(VideoQualityController).Assembly.GetManifestResourceStream("Mambo.Shaders." + name);
        if (expected is null) throw Failure("画质资源缺失。");
        using var actual = File.OpenRead(path);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(expected), SHA256.HashData(actual)))
            throw Failure("画质资源校验失败。");
        return [path];
    }

    private sealed record RenderPass(string Description, string Timing);

    private static Dictionary<string, RenderPass[]> Passes(MpvValue? value)
    {
        var result = new Dictionary<string, RenderPass[]>(StringComparer.Ordinal);
        if (value is not MpvValue.Map map) return result;
        foreach (var (kind, list) in map.Values)
        {
            if (list is not MpvValue.Array array) continue;
            result[kind] = array.Values.OfType<MpvValue.Map>().Select(pass => new RenderPass(
                pass.Values.GetValueOrDefault("desc") is MpvValue.Text text ? text.Value : "",
                Fingerprint(pass))).ToArray();
        }
        return result;
    }

    private static string Fingerprint(MpvValue value)
    {
        var builder = new StringBuilder();
        Append(value);
        return builder.ToString();
        void Append(MpvValue? entry)
        {
            switch (entry)
            {
                case MpvValue.Map map:
                    foreach (var pair in map.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal)) { builder.Append(pair.Key); Append(pair.Value); }
                    break;
                case MpvValue.Array array: foreach (var item in array.Values) Append(item); break;
                case MpvValue.Text text: builder.Append(text.Value); break;
                case MpvValue.Number number: builder.Append(number.Value.ToString("R", CultureInfo.InvariantCulture)); break;
                case MpvValue.WholeNumber number: builder.Append(number.Value.ToString(CultureInfo.InvariantCulture)); break;
                case MpvValue.Flag flag: builder.Append(flag.Value); break;
            }
            builder.Append('|');
        }
    }

    private static void ValidateMode(VideoQualityMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
    }

    private static AppException Failure(string text) => new(new(AppErrorKind.Player, "player.video_quality_failed", text, false));

    public async ValueTask DisposeAsync()
    {
        core.ShaderFailed -= OnShaderFailed;
        await lifetime.CancelAsync().ConfigureAwait(false);
        await writer.WaitAsync().ConfigureAwait(false);
        writer.Dispose();
        lifetime.Dispose();
    }
}
