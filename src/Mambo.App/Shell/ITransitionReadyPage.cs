namespace Mambo.App.Shell;

/// <summary>首屏结果和历史滚动位置已可呈现；不等待卡片图片。</summary>
internal interface ITransitionReadyPage
{
    Task WaitForPresentationAsync(CancellationToken cancellationToken);
}
