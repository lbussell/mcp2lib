using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Mcp2Lib;

public static class ToolDiscovery
{
    public static async Task<IReadOnlyList<Tool>> DiscoverAsync(
        McpClient client, CancellationToken cancellationToken = default)
    {
        var tools = new List<Tool>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await client.ListToolsAsync(
                new ListToolsRequestParams { Cursor = cursor }, cancellationToken);
            tools.AddRange(page.Tools);
            cursor = page.NextCursor;
            if (cursor is not null && !cursors.Add(cursor))
                throw new InvalidOperationException("The server repeated a tools/list cursor.");
        } while (cursor is not null);

        if (tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != tools.Count)
            throw new InvalidOperationException("The server returned duplicate tool names.");

        return tools.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray();
    }
}
