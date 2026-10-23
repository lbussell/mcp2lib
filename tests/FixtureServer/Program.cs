using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

var mode = args.FirstOrDefault() ?? "normal";
var schema = JsonDocument.Parse("""
    {
      "type":"object",
      "properties":{
        "text":{"type":"string"},
        "count":{"type":"integer"},
        "enabled":{"type":"boolean"},
        "tags":{"type":"array","items":{"type":"string"}},
        "payload":{"type":"object"},
        "nullable":{"type":["string","null"]}
      },
      "required":["text"]
    }
    """).RootElement.Clone();
var options = new McpServerOptions
{
    ServerInfo = new Implementation { Name = "mcp2lib-fixture", Version = "1.0" },
    Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
    Handlers = new McpServerHandlers
    {
        ListToolsHandler = async (context, cancellationToken) =>
        {
            if (mode == "slow")
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            var firstPage = context.Params?.Cursor is null;
            return new ListToolsResult
            {
                Tools = [new Tool { Name = firstPage || mode == "duplicates" ? "echo" : "fail", InputSchema = schema }],
                NextCursor = firstPage || mode == "loop" ? "page-2" : null
            };
        },
        CallToolHandler = (context, _) =>
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var (key, value) in context.Params?.Arguments ?? new Dictionary<string, JsonElement>())
                {
                    writer.WritePropertyName(key);
                    value.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            return ValueTask.FromResult(new CallToolResult
            {
                IsError = context.Params?.Name == "fail",
                Content = [new TextContentBlock { Text = Encoding.UTF8.GetString(stream.ToArray()) }]
            });
        }
    }
};

if (mode == "http")
{
    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    builder.Services.AddMcpServer(serverOptions =>
    {
        serverOptions.ServerInfo = options.ServerInfo;
        serverOptions.Capabilities = options.Capabilities;
        serverOptions.Handlers = options.Handlers;
    }).WithHttpTransport(transport => transport.Stateless = true);
    await using var app = builder.Build();
    app.Urls.Add("http://127.0.0.1:0");
    app.MapMcp("/mcp");
    await app.StartAsync();
    Console.WriteLine(app.Urls.Single() + "/mcp");
    await app.WaitForShutdownAsync();
}
else
{
    await using var server = McpServer.Create(new StdioServerTransport(options), options);
    await server.RunAsync();
}
