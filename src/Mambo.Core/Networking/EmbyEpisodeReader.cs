using System.Globalization;
using Mambo.Core.Contracts;
using Mambo.Core.Session;

namespace Mambo.Core.Networking;

/// <summary>按服务器实际返回条数分页读取剧集，单页大小不作为整季集数上限。</summary>
internal sealed class EmbyEpisodeReader(EmbyApi api, RequestScheduler? scheduler = null)
{
    private const int PageSize = 500;

    public async Task<EmbyItem[]> ReadAllAsync(AccountSession account, string parentId, string fields, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, account.Token);
        var cancellation = linked.Token;
        var path = "Users/" + EmbyApi.Escape(account.Secret.UserId) + "/Items?ParentId=" + EmbyApi.Escape(parentId) +
            "&IncludeItemTypes=Episode&Recursive=true&SortBy=ParentIndexNumber,IndexNumber,SortName&SortOrder=Ascending&Limit=" +
            PageSize.ToString(CultureInfo.InvariantCulture) + "&Fields=" + EmbyApi.Escape(fields) + "&StartIndex=";
        var items = new List<EmbyItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var pagePath = path + offset.ToString(CultureInfo.InvariantCulture);
            var response = await (scheduler is null ? api.ItemsAsync(account, pagePath, cancellation) :
                scheduler.RunAsync(ct => api.ItemsAsync(account, pagePath, ct), scopeToken: cancellation)).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            var rows = response.Items ?? [];
            if (rows.Length > PageSize || response.TotalRecordCount < 0 || offset > int.MaxValue - rows.Length)
                throw InvalidResponse();
            if (rows.Length == 0)
            {
                if (response.TotalRecordCount is { } total && offset < total) throw InvalidResponse();
                break;
            }

            var progressed = false;
            foreach (var row in rows)
                if (EmbyMapper.Identity(row?.Id) is { } id && seen.Add(id)) progressed = true;
            // 兼容忽略 StartIndex 的响应，避免反复请求同一页；声称还有数据时不把残缺列表当作完整计划。
            if (offset > 0 && !progressed)
            {
                if (response.TotalRecordCount is { } total && offset < total) throw InvalidResponse();
                break;
            }
            items.AddRange(rows);
            offset += rows.Length;
            if (response.TotalRecordCount is { } count && offset >= count) break;
            // 未提供总数时继续探测至空页，不能把服务器限制的短页误认为末页。
        }
        return items.ToArray();
    }

    private static AppException InvalidResponse() => new(ErrorText.InvalidResponse("加载剧集"));
}
