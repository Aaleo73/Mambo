namespace Mambo.App.Shell;

/// <summary>CancelText 为 null 时只显示一个按钮。</summary>
public sealed class ConfirmRequest(string title, string text, string confirmText, bool danger = false, string? cancelText = "取消")
{
    public string Title { get; } = title;
    public string Text { get; } = text;
    public string ConfirmText { get; } = confirmText;
    public bool Danger { get; } = danger;
    public string? CancelText { get; } = cancelText;
}

public interface IDialogPresenter
{
    Task<bool> PresentAsync(ConfirmRequest request);
}

/// <summary>模态确认框，多个请求排队依次显示；Esc 或点击背景等同于取消。</summary>
public sealed class DialogService : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IDialogPresenter? presenter;

    public bool IsOpen { get; private set; }

    public void Attach(IDialogPresenter host) => presenter = host;

    public async Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (presenter is null) return false;
        await gate.WaitAsync();
        try
        {
            IsOpen = true;
            return await presenter.PresentAsync(request);
        }
        finally
        {
            IsOpen = false;
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();
}
