using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Mcp2Lib.Tests;

public sealed class CliTests
{
    [Theory]
    [InlineData("list", "Specify either")]
    [InlineData("list --timeout 0 -- nonexistent-server", "--timeout")]
    [InlineData("list --endpoint ftp://example.test/mcp", "HTTP")]
    [InlineData("list --endpoint https://example.test/mcp -- nonexistent-server", "not both")]
    [InlineData("generate --output unused --namespace bad-name -- nonexistent-server", "--namespace")]
    [InlineData("generate -- nonexistent-server", "--output")]
    [InlineData("list -- mcp2lib-nonexistent-test-server", "mcp2lib:")]
    public async Task InvalidInputReturnsNonzeroWithoutConnecting(string arguments, string expected)
    {
        var result = await RunAsync(arguments.Split(' '));
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expected, result.Output);
    }

    [Fact]
    public async Task ListProducesParseableCompleteJson()
    {
        var result = await RunAsync(["list", "--", "dotnet", DiscoveryTests.FixturePath]);
        Assert.True(result.ExitCode == 0, result.Output);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal(2, document.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task TimeoutFailsWithAnActionableMessage()
    {
        var result = await RunAsync(["list", "--timeout", "1", "--", "dotnet", DiscoveryTests.FixturePath, "slow"]);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("timed out", result.Output);
    }

    [Fact]
    public async Task ListsToolsOverRealStreamableHttp()
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(DiscoveryTests.FixturePath);
        start.ArgumentList.Add("http");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start HTTP fixture.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            var endpoint = await process.StandardOutput.ReadLineAsync(timeout.Token);
            Assert.False(string.IsNullOrEmpty(endpoint), process.HasExited ? await errors : "No HTTP endpoint.");
            var result = await RunAsync(["list", "--endpoint", endpoint!]);
            Assert.True(result.ExitCode == 0, result.Output);
            using var document = JsonDocument.Parse(result.Output);
            Assert.Equal(2, document.RootElement.GetArrayLength());
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string[] args)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var path = Path.Combine(DiscoveryTests.Root, "src", "Mcp2Lib", "bin", configuration, "net11.0", "mcp2lib.dll");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await CompilationTests.RunAsync("dotnet", [path, .. args], timeout.Token);
    }
}
