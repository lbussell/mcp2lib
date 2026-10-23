using System.Diagnostics;
using Mcp2Lib;
using Xunit;

namespace Mcp2Lib.Tests;

public sealed class CompilationTests
{
    [Fact]
    public async Task GeneratedLibrariesCompileAndCallToolsWithoutReflectionSerialization()
    {
        var directory = Path.Combine(DiscoveryTests.Root, "artifacts", "tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await using var client = await DiscoveryTests.ConnectAsync("normal", timeout.Token);
            var tools = (await ToolDiscovery.DiscoverAsync(client, timeout.Token)).ToList();
            var hostile = GeneratorTests.Tool("quote\"\\\n", """
                {"type":"object","properties":{
                  "a-b":{"type":"string"},"a_b":{"type":"integer"},
                  "quote\"\\":{"type":"object"},"extraArguments":{"type":"boolean"},
                  "quoteArguments":{"type":"string"},
                  "matrix":{"type":"array","items":{"type":"array","items":{"type":"number"}}}
                }}
                """);
            hostile.Description = "</summary>\u2028#error not executable\u0085<code>&\u2029done";
            tools.AddRange([hostile, GeneratorTests.Tool("a-b"), GeneratorTests.Tool("a_b"),
                GeneratorTests.Tool("connect"), GeneratorTests.Tool("123"), GeneratorTests.Tool("!!!")]);
            LibraryGenerator.WriteFiles(Path.Combine(directory, "Generated"),
                LibraryGenerator.Generate(tools, "Example", "Tools"), false);
            LibraryGenerator.WriteFiles(Path.Combine(directory, "Empty"),
                LibraryGenerator.Generate([], "Empty", "EmptyTools"), false);
            File.WriteAllText(Path.Combine(directory, "Runner.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net11.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                    <JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Program.cs" />
                    <ProjectReference Include="Generated\Tools.csproj" />
                    <ProjectReference Include="Empty\EmptyTools.csproj" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(directory, "Program.cs"), """"
                using System.Text.Json;
                using Example;
                using ModelContextProtocol.Client;
                using ModelContextProtocol.Protocol;

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Command = "dotnet", Arguments = [args[0]]
                });
                await using var session = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
                await using (var borrowed = new Tools(session))
                {
                    var result = await borrowed.EchoAsync(new()
                    {
                        Text = "hello", Count = 0, Enabled = false, Tags = [],
                        Nullable = JsonDocument.Parse("null").RootElement.Clone(),
                        Payload = JsonDocument.Parse("""{"nested":true}""").RootElement.Clone(),
                        ExtraArguments = new() { ["extension"] = JsonDocument.Parse("42").RootElement.Clone() }
                    }, timeout.Token);
                    using var json = JsonDocument.Parse(((TextContentBlock)result.Content[0]).Text);
                    var root = json.RootElement;
                    if (root.GetProperty("text").GetString() != "hello" ||
                        root.GetProperty("count").GetInt64() != 0 || root.GetProperty("enabled").GetBoolean() ||
                        root.GetProperty("tags").GetArrayLength() != 0 ||
                        root.GetProperty("nullable").ValueKind != JsonValueKind.Null ||
                        !root.GetProperty("payload").GetProperty("nested").GetBoolean() ||
                        root.GetProperty("extension").GetInt32() != 42)
                        throw new Exception("Wire arguments changed.");
                    var minimal = await borrowed.EchoAsync(new() { Text = "minimal" }, timeout.Token);
                    using var minimalJson = JsonDocument.Parse(((TextContentBlock)minimal.Content[0]).Text);
                    if (minimalJson.RootElement.EnumerateObject().Count() != 1)
                        throw new Exception("Unset optional arguments must be omitted.");
                    var failure = await borrowed.FailAsync(new() { Text = "error" }, timeout.Token);
                    if (failure.IsError != true) throw new Exception("Tool error lost.");
                }
                await session.ListToolsAsync(cancellationToken: timeout.Token);
                Console.WriteLine("WIRE_OK");
                """");
            var result = await RunAsync("dotnet",
                ["run", "--project", Path.Combine(directory, "Runner.csproj"), "--", DiscoveryTests.FixturePath],
                timeout.Token);
            Assert.True(result.ExitCode == 0, result.Output);
            Assert.Contains("WIRE_OK", result.Output);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static async Task<(int ExitCode, string Output)> RunAsync(
        string command, IEnumerable<string> arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start test process.");
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            return (process.ExitCode, await stdout + await stderr);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }
}
