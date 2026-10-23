using Mcp2Lib;
using ModelContextProtocol.Client;
using Xunit;

namespace Mcp2Lib.Tests;

public sealed class DiscoveryTests
{
    [Fact]
    public async Task FollowsAllToolPages()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await ConnectAsync("normal", timeout.Token);
        var tools = await ToolDiscovery.DiscoverAsync(client, timeout.Token);
        Assert.Equal(["echo", "fail"], tools.Select(tool => tool.Name));
    }

    [Theory]
    [InlineData("loop", "cursor")]
    [InlineData("duplicates", "duplicate")]
    public async Task RejectsBrokenPagination(string mode, string error)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await ConnectAsync(mode, timeout.Token);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ToolDiscovery.DiscoverAsync(client, timeout.Token));
        Assert.Contains(error, exception.Message);
    }

    [Fact]
    public async Task DiscoveryIsCancellable()
    {
        using var connectionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await ConnectAsync("slow", connectionTimeout.Token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ToolDiscovery.DiscoverAsync(client, timeout.Token));
    }

    internal static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "mcp2lib.md")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Cannot locate test repository.");
        }
    }

    internal static string FixturePath =>
        Path.Combine(Root, "tests", "FixtureServer", "bin",
            new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net11.0", "FixtureServer.dll");

    internal static Task<McpClient> ConnectAsync(string mode, CancellationToken token) =>
        McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [FixturePath, mode]
        }), cancellationToken: token);
}
