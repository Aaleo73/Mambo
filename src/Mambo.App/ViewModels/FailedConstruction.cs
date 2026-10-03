namespace Mambo.App.ViewModels;

/// <summary>构造过程中尚未交给页面持有的资源；一项清理失败仍继续释放其余项。</summary>
internal static class FailedConstruction
{
    public static void Release(params Action[] actions)
    {
        foreach (var action in actions)
            try { action(); }
            catch (Exception) { }
    }
}
