using CommunityToolkit.Mvvm.ComponentModel;
using Mambo.Core.Contracts;

namespace Mambo.App.ViewModels;

/// <summary>最近播放页：继续观看分页（每页 120），页头显示已加载数量。</summary>
public sealed partial class RecentViewModel : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource scope = new();

    public RecentViewModel(ILibraryService library)
    {
        ArgumentNullException.ThrowIfNull(library);
        Cards = new PagedCards(library.ObserveRecent(120, scope.Token), CardContext.ContinueWatching, landscape: true);
        Cards.Items.CollectionChanged += (_, _) => UpdateCount();
        Cards.PropertyChanged += (_, _) => UpdateCount();
        UpdateCount();
    }

    public PagedCards Cards { get; }

    [ObservableProperty]
    public partial string CountText { get; private set; } = "";

    public void Dispose()
    {
        scope.Cancel();
        Cards.Dispose();
        scope.Dispose();
    }

    private void UpdateCount() => CountText = Cards.IsInitialized ? $"{Cards.Items.Count} 项" : "";
}
