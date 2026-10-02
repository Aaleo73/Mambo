using Mambo.Core.Contracts;
using Mambo.Core.Persistence;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Parsing;

namespace Mambo.Core.Diagnostics;

/// <summary>固定事件名、安全错误码与中文模板；格式化完成后再次脱敏，禁止记录异常对象与认证DTO。</summary>
public sealed class AppLog : IDisposable
{
    private readonly Logger logger;
    public AppLog(AppPaths paths)
    {
        logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.File(new SafeFormatter(), Path.Combine(paths.Logs, "mambo-.log"),
            rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7, fileSizeLimitBytes: 10 * 1024 * 1024, rollOnFileSizeLimit: true, shared: true).CreateLogger();
        AppDomain.CurrentDomain.UnhandledException += Unhandled;
        TaskScheduler.UnobservedTaskException += Unobserved;
    }
    public void Error(string operation, AppError error) => Write(operation + ": " + error.Kind + "/" + error.Code + ": " + error.Message +
        (error.DiagnosticId is { } diagnostic ? " (" + diagnostic + ")" : ""));
    public void Exception(string operation, Exception exception) => Write(operation + ": " + exception.GetType().Name + " (" + exception.HResult.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
    // 仅构造字面量事件，绕过 Serilog 对任意对象的反射解构，使日志路径可在 AOT 中裁剪。
    private void Write(string message) => logger.Write(new LogEvent(DateTimeOffset.Now, LogEventLevel.Error, null,
        new MessageTemplate(message, [new TextToken(message)]), []));
    private void Unhandled(object sender, UnhandledExceptionEventArgs args)
    { if (args.ExceptionObject is Exception exception) Exception("未处理异常", exception); }
    private void Unobserved(object? sender, UnobservedTaskExceptionEventArgs args) => Exception("未观察任务异常", args.Exception);
    public void Dispose()
    {
        AppDomain.CurrentDomain.UnhandledException -= Unhandled;
        TaskScheduler.UnobservedTaskException -= Unobserved;
        logger.Dispose();
    }
    private sealed class SafeFormatter : ITextFormatter
    {
        public void Format(LogEvent logEvent, TextWriter output) => output.WriteLine("{0:O} [{1}] {2}", logEvent.Timestamp, logEvent.Level, UrlRedactor.Redact(logEvent.RenderMessage(System.Globalization.CultureInfo.InvariantCulture)));
    }
}
