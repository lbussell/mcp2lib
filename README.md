# mcp2lib

Turn an MCP server's tools into a .NET library you can call without an LLM.
The CLI uses System.CommandLine and the official `ModelContextProtocol.Core`
SDK, targets .NET 11, and publishes as a Native AOT executable.

## Build

Install the .NET 11 SDK selected by `global.json`. Native publishing also
requires the platform's [.NET Native AOT prerequisites](https://learn.microsoft.com/dotnet/core/deploying/native-aot/).
On Windows, install Visual Studio C++ build tools and a Windows SDK.

This repository's `NuGet.Config` uses the company mirror at
`https://packagefeedproxy.microsoft.io/nuget/v3/index.json`; no restore contacts
nuget.org. Outside that environment, replace the source with an accessible feed.

```powershell
dotnet build
dotnet run --project .\tests\Mcp2Lib.Tests
dotnet publish .\src\Mcp2Lib -c Release -r win-arm64 -o .\artifacts\cli
.\artifacts\cli\mcp2lib.exe --help
```

Choose your own runtime identifier (for example, `win-x64` or `linux-x64`) when
publishing on another platform. Native AOT publishing requires a compatible
host toolchain; it is not a general cross-OS compiler.

## Generate a library

For a local stdio server, put its executable and all arguments after `--`.
The executable is started directly, not passed to a shell. Quote paths and
arguments containing spaces. Its working directory and environment are inherited.
Only run servers you trust.

```powershell
.\artifacts\cli\mcp2lib.exe generate `
  --output .\generated\MyTools --namespace MyApp.Tools --class-name MyToolsClient `
  -- my-mcp-server --its-option value
```

For a remote server, use `--endpoint` instead of a command:

```powershell
.\artifacts\cli\mcp2lib.exe generate `
  --output .\generated\Remote --endpoint https://example.com/mcp
```

The SDK handles Streamable HTTP and legacy SSE detection. The CLI supports
unauthenticated HTTP endpoints; for authenticated connections, use the generated
library with your configured SDK transport/client.

`list` prints the full discovered tool definitions as JSON without invoking
tools. Both commands support `--timeout` (seconds, default 120) and
`--protocol-version` (by default, the SDK negotiates). Child-process diagnostics
go to stderr. Errors return a nonzero exit code.

Generation creates three files:

| File | Purpose |
| --- | --- |
| `<ClassName>.csproj` | Standalone, AOT-compatible .NET 11 library referencing the official SDK |
| `<ClassName>.g.cs` | Client methods, input models, and source-generated JSON serialization |
| `tools.json` | Discovered schemas and metadata, including output schemas when supplied |

The CLI follows all tool-list pages and produces deterministic output. Existing
files are protected unless `--force` is supplied; unrelated files are left alone.
Namespaces and class names must use ASCII C# identifiers. Tool/property names
are sanitized and collisions disambiguated, while their original wire names are
preserved.

## Use a generated client

Add a project reference to the generated project. A client can start a stdio
server or connect through any official SDK `IClientTransport`:

```csharp
using ModelContextProtocol.Client;
using MyApp.Tools;

await using var tools = await MyToolsClient.ConnectAsync(
    new StdioClientTransport(new()
    {
        Command = "my-mcp-server",
        Arguments = ["--its-option", "value"]
    }),
    cancellationToken: cancellationToken);

// The actual method and argument names come from the server's tools.
var result = await tools.SearchAsync(new() { Query = "example" }, cancellationToken);
if (result.IsError == true)
    throw new InvalidOperationException("The MCP tool reported an error.");
```

`ConnectAsync` owns the resulting session; disposing it shuts down the connection
and its child process. `new MyToolsClient(existingMcpClient)` borrows a session
without owning it (use `ownsClient: true` to transfer ownership).

String, integer, number, and boolean inputs map to `string`, `long`, `double`,
and `bool`; arrays map recursively. Complex objects, references, unions, and
untyped schemas use `JsonElement` so arbitrary JSON remains representable.
This is not a full JSON Schema-to-C# compiler: schema constraints, formats,
defaults, and enum values remain in `tools.json` and are enforced by the server.

Required properties use C# `required`. Unset optional properties are omitted,
letting server defaults apply; explicit `false`, `0`, and empty arrays are sent.
For JSON-valued fields, use a `JsonElement` containing JSON `null` to send explicit
null. `ExtraArguments` accepts additional wire fields, or explicit null for a
typed optional field left unset; do not duplicate a populated typed property.

Methods return the SDK's `CallToolResult`, preserving `IsError`, structured
content, text, images, and other content blocks. Protocol/transport exceptions
propagate normally. Output schemas are recorded, but no result DTOs are invented
for servers that omit them.

## Tests

The tests use a local fixture server. They cover pagination, cancellation,
malformed pagination, naming/escaping, overwrite protection, compilation of
generated projects, actual tool calls with reflection-based JSON serialization
disabled, CLI failures, and real Streamable HTTP discovery.
