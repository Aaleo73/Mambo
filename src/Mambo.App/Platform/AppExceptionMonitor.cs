using Mambo.Core.Diagnostics;
using Microsoft.UI.Xaml;

namespace Mambo.App.Platform;

/// <summary>补上 WinUI 的异常入口；记录安全诊断，不改变框架的异常处理行为。</summary>
internal sealed class AppExceptionMonitor : IDisposable
{
    private readonly Application application;
    private readonly AppLog log;
    public AppExceptionMonitor(Application application, AppLog log)
    {
        this.application = application; this.log = log;
        application.UnhandledException += Unhandled;
    }
    private void Unhandled(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args) => log.Exception("WinUI 未处理异常", args.Exception);
    public void Dispose() => application.UnhandledException -= Unhandled;
}
