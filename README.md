# mcp2lib

`mcp2lib` connects to a Model Context Protocol server, reads its tool definitions, and generates a standalone .NET 11 client library. Use the generated library to call MCP tools directly from C# without an LLM in the loop.

Both `mcp2lib` and the libraries it generates use the official `ModelContextProtocol.Core` SDK and support Native AOT compilation.

## Build the CLI

Before you build `mcp2lib`, check these requirements:

- Install the .NET 11 SDK.
- If you want to publish a Native AOT executable, install the [.NET Native AOT prerequisites](https://learn.microsoft.com/dotnet/core/deploying/native-aot/) for your platform. On Windows, install the Visual Studio C++ build tools and the Windows SDK.

Build the project from the repository root:

```powershell
dotnet build
```

To publish a self-contained Native AOT executable, pass your host platform's runtime identifier, such as `win-arm64`, `win-x64`, or `linux-x64`:

```powershell
dotnet publish .\src\Mcp2Lib -c Release -r win-arm64 -o .\artifacts\cli
.\artifacts\cli\mcp2lib.exe --help
```

Native AOT publishing requires a host toolchain for the target operating system and does not cross-compile across operating systems.

## Generate a client library

You can generate a library from either a local stdio server or a remote HTTP server.

### Connect to a local stdio server

Put the server executable and its arguments after `--`. Quote any paths or arguments that contain spaces.

```powershell
.\artifacts\cli\mcp2lib.exe generate `
  --output .\generated\MyTools --namespace MyApp.Tools --class-name MyToolsClient `
  -- my-mcp-server --its-option value
```

When tool discovery succeeds, `mcp2lib` prints the number of generated tools and the output path:

```text
Generated 4 tools in D:\src\mcp2lib\generated\MyTools
```

`mcp2lib` starts the executable directly without a shell and forwards the child process's standard error output to `stderr`. The child process inherits the current working directory and environment variables. Run only MCP servers that you trust.

### Connect to a remote HTTP server

Pass `--endpoint` with an `http` or `https` URL instead of a server command:

```powershell
.\artifacts\cli\mcp2lib.exe generate `
  --output .\generated\Remote --endpoint https://example.com/mcp
```

The SDK detects whether the server uses Streamable HTTP or legacy SSE. The CLI connects without authentication headers. If your server requires authentication, generate the library from an unauthenticated environment or schema source, and then pass a configured SDK transport or `McpClient` at runtime.

### Inspect tools without generating files

Use the `list` command to print the server's discovered tool definitions as JSON to standard output without invoking any tools:

```powershell
.\artifacts\cli\mcp2lib.exe list -- my-mcp-server --its-option value
```

### Command options and generated files

Both `generate` and `list` accept these discovery options:

- `--timeout <seconds>` sets the discovery timeout in seconds. The default is `120`.
- `--protocol-version <version>` requests a specific MCP protocol version. By default, the SDK negotiates the version with the server.

`generate` also accepts `--output` or `-o`, `--namespace` with default `Generated`, `--class-name` with default `McpToolsClient`, and `--force`.

`generate` follows all tool-list pages and writes three deterministic files to the output directory:

| File | Purpose |
| --- | --- |
| `<ClassName>.csproj` | Standalone, AOT-compatible .NET 11 class library that references `ModelContextProtocol.Core` |
| `<ClassName>.g.cs` | Generated client class, input models, and source-generated JSON serialization context |
| `tools.json` | Discovered tool schemas and metadata, including output schemas when the server provides them |

`generate` fails if any of the three target files already exist unless you pass `--force`. It leaves unrelated files in the output directory untouched.

Namespaces and class names must be valid ASCII C# identifiers. `mcp2lib` converts tool and property names into valid C# identifiers, appends a numeric suffix when names collide, and preserves the original wire names in JSON attributes and tool calls.

## Use a generated library

Add a project reference to the generated `.csproj` file, create a transport, and call the generated tool methods:

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

var result = await tools.SearchAsync(new() { Query = "example" }, cancellationToken);
if (result.IsError == true)
    throw new InvalidOperationException("The MCP tool reported an error.");
```

`ConnectAsync` creates and owns the `McpClient` session, so disposing the generated client closes the connection and stops any child process. To wrap an existing `McpClient` without transferring ownership, call `new MyToolsClient(existingMcpClient)`. Pass `ownsClient: true` to that constructor if the generated client should dispose the `McpClient`.

### Input models and results

The generator maps JSON Schema types to C# properties as follows:

- `string`, `integer`, `number`, and `boolean` map to `string`, `long`, `double`, and `bool`.
- `array` maps recursively to a C# array of the item type.
- Objects, `$ref`, `anyOf`, `oneOf`, `allOf`, and untyped schemas map to `JsonElement` so that callers can pass arbitrary JSON values.

`mcp2lib` does not compile full JSON Schema validation rules into C# types. Instead, `tools.json` records schema constraints, formats, defaults, and enum values, and the server enforces them at call time.

Required schema properties generate `required` C# properties. The serializer omits unset optional properties so that server defaults apply, and it sends explicit `false`, `0`, and empty array values. For `JsonElement` properties, pass a `JsonElement` that represents JSON `null` when you need to send an explicit null value. Each input model also includes an `ExtraArguments` dictionary for undeclared wire fields or explicit null values on unset optional properties. Do not add a key to `ExtraArguments` when the corresponding typed property is already set.

Each tool method returns the SDK's `CallToolResult`, which contains `IsError`, structured content, text, images, and other content blocks. Protocol and transport exceptions propagate to the caller.

## Run the tests

Run the test suite from the repository root:

```powershell
dotnet run --project .\tests\Mcp2Lib.Tests
```

The test suite runs against a local fixture MCP server in `tests\FixtureServer` and verifies tool pagination, cancellation, malformed pagination responses, identifier naming and escaping, file overwrite protection, compilation of generated projects, tool calls with reflection-based JSON serialization disabled, CLI error handling, and Streamable HTTP discovery.

