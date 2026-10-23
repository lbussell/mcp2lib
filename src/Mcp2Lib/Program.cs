using System.CommandLine;
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace Mcp2Lib;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var root = new RootCommand("Generate an AOT-compatible .NET client library from an MCP server.");
        var generate = new Command("generate", "Discover tools and generate a standalone .NET library.");
        var list = new Command("list", "Print discovered tool definitions as JSON without invoking any tools.");
        root.Subcommands.Add(generate);
        root.Subcommands.Add(list);

        foreach (var command in new[] { generate, list })
        {
            var endpoint = new Option<string?>("--endpoint") { Description = "HTTP MCP endpoint instead of a stdio command." };
            var timeout = new Option<int>("--timeout") { DefaultValueFactory = _ => 120, Description = "Discovery timeout in seconds." };
            var protocolVersion = new Option<string?>("--protocol-version") { Description = "Request a specific MCP version instead of SDK auto-negotiation." };
            var server = new Argument<string[]>("server") { Arity = ArgumentArity.ZeroOrMore, Description = "Executable and arguments (put after --)." };
            command.Options.Add(endpoint);
            command.Options.Add(timeout);
            command.Options.Add(protocolVersion);
            command.Arguments.Add(server);

            var output = new Option<string>("--output", "-o") { Required = true, Description = "Output directory." };
            var ns = new Option<string>("--namespace") { DefaultValueFactory = _ => "Generated", Description = "Generated C# namespace." };
            var className = new Option<string>("--class-name") { DefaultValueFactory = _ => "McpToolsClient", Description = "Generated client class and project name." };
            var force = new Option<bool>("--force") { Description = "Replace existing generated files." };
            if (command == generate)
            {
                command.Options.Add(output);
                command.Options.Add(ns);
                command.Options.Add(className);
                command.Options.Add(force);
            }

            command.SetAction(async (parse, cancellationToken) =>
            {
                try
                {
                    var serverArgs = parse.GetValue(server)!;
                    var endpointValue = parse.GetValue(endpoint);
                    var seconds = parse.GetValue(timeout);
                    if ((endpointValue is null) == (serverArgs.Length == 0))
                        throw new ArgumentException("Specify either --endpoint or a stdio executable after --, not both.");
                    Uri? uri = null;
                    if (endpointValue is not null && (!Uri.TryCreate(endpointValue, UriKind.Absolute, out uri) ||
                        uri.Scheme is not ("http" or "https")))
                        throw new ArgumentException("--endpoint must be an absolute HTTP or HTTPS URL.");
                    if (seconds <= 0)
                        throw new ArgumentException("--timeout must be greater than zero.");

                    if (command == generate)
                        LibraryGenerator.ValidateNames(parse.GetValue(ns)!, parse.GetValue(className)!);

                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
                    IClientTransport transport = uri is not null
                        ? new HttpClientTransport(new HttpClientTransportOptions { Endpoint = uri })
                        : new StdioClientTransport(new StdioClientTransportOptions
                        {
                            Command = serverArgs[0],
                            Arguments = serverArgs[1..],
                            StandardErrorLines = line => Console.Error.WriteLine(line)
                        });
                    await using var client = await McpClient.CreateAsync(transport,
                        new McpClientOptions { ProtocolVersion = parse.GetValue(protocolVersion) },
                        cancellationToken: deadline.Token);
                    var tools = await ToolDiscovery.DiscoverAsync(client, deadline.Token);
                    if (command == list)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(tools.ToArray(), ToolJsonContext.Default.ToolArray));
                    }
                    else
                    {
                        var files = LibraryGenerator.Generate(tools, parse.GetValue(ns)!, parse.GetValue(className)!);
                        LibraryGenerator.WriteFiles(parse.GetValue(output)!, files, parse.GetValue(force));
                        Console.WriteLine($"Generated {tools.Count} tools in {Path.GetFullPath(parse.GetValue(output)!)}");
                    }
                    return 0;
                }
                catch (OperationCanceledException)
                {
                    Console.Error.WriteLine(cancellationToken.IsCancellationRequested
                        ? "Cancelled."
                        : "MCP discovery timed out. Check the server command, authentication, or --timeout.");
                    return cancellationToken.IsCancellationRequested ? 130 : 1;
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                    or IOException or UnauthorizedAccessException or HttpRequestException or McpException
                    or Win32Exception or JsonException)
                {
                    Console.Error.WriteLine($"mcp2lib: {exception.Message}");
                    return 1;
                }
            });
        }

        return await root.Parse(args).InvokeAsync();
    }
}
