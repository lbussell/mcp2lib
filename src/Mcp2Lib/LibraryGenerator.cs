using System.Globalization;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Mcp2Lib;

public static class LibraryGenerator
{
    public const string McpPackageVersion = "2.2.0";

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "McpClient", "McpClientOptions", "IClientTransport", "CallToolResult", "CallToolRequestParams",
        "Task", "ValueTask", "CancellationToken", "IAsyncDisposable", "ArgumentNullException",
        "Dictionary", "JsonElement", "JsonSerializer", "JsonSerializerContext", "JsonContext",
        "JsonPropertyName", "JsonIgnore", "JsonIgnoreCondition", "JsonExtensionData",
        "JsonSerializable", "JsonSourceGenerationOptions", "StringComparer", "Object",
        "ConnectAsync", "DisposeAsync", "ToString", "GetType", "Equals", "GetHashCode",
        "ReferenceEquals", "MemberwiseClone", "Finalize"
    };

    private static readonly HashSet<string> Keywords = new(
        ("abstract as base bool break byte case catch char checked class const continue decimal default " +
         "delegate do double else enum event explicit extern false finally fixed float for foreach goto " +
         "if implicit in int interface internal is lock long namespace new null object operator out override " +
         "params private protected public readonly ref return sbyte sealed short sizeof stackalloc static " +
         "string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual " +
         "void volatile while file required scoped record").Split(' '), StringComparer.Ordinal);

    public static void ValidateNames(string ns, string className)
    {
        if (string.IsNullOrWhiteSpace(ns) || ns.Split('.').Any(part => !IsIdentifier(part)))
            throw new ArgumentException("--namespace must be a dot-separated C# identifier (without keywords).");
        if (!IsIdentifier(className) || Reserved.Contains(className))
            throw new ArgumentException("--class-name must be a C# identifier that does not conflict with generated SDK types.");
    }

    public static IReadOnlyDictionary<string, string> Generate(
        IReadOnlyList<Tool> tools, string ns, string className)
    {
        ValidateNames(ns, className);
        var names = new HashSet<string>(Reserved, StringComparer.Ordinal) { className };
        var toolNames = new HashSet<string>(StringComparer.Ordinal);
        var methods = new StringBuilder();
        var models = new StringBuilder();
        var context = new StringBuilder();

        foreach (var tool in tools.OrderBy(tool => tool.Name, StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(tool.Name) || !toolNames.Add(tool.Name))
                throw new ArgumentException("Tool names must be nonempty and unique.");
            if (tool.InputSchema.ValueKind != JsonValueKind.Object)
                throw new ArgumentException($"Tool '{tool.Name}' has a non-object input schema.");

            var stem = Identifier(tool.Name);
            var method = Unique(stem + "Async", names);
            var model = Unique(stem + "Arguments", names);
            var propertyNames = new HashSet<string>(Reserved, StringComparer.Ordinal) { model, "ExtraArguments" };
            var required = tool.InputSchema.TryGetProperty("required", out var requiredElement)
                ? requiredElement.EnumerateArray().Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal)
                : [];

            AppendSummary(methods, tool.Description ?? tool.Name, "    ");
            methods.AppendLine($$"""
                public ValueTask<CallToolResult> {{method}}(
                    {{model}} arguments, CancellationToken cancellationToken = default)
                {
                    ArgumentNullException.ThrowIfNull(arguments);
                    var json = JsonSerializer.SerializeToElement(arguments, JsonContext.Default.{{model}});
                    return _client.CallToolAsync(new CallToolRequestParams
                    {
                        Name = {{Literal(tool.Name)}},
                        Arguments = json.EnumerateObject().ToDictionary(property => property.Name, property => property.Value)
                    }, cancellationToken);
                }

            """);

            models.AppendLine($"public sealed class {model}");
            models.AppendLine("{");
            if (tool.InputSchema.TryGetProperty("properties", out var properties))
            {
                foreach (var property in properties.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    var propertyName = Unique(Identifier(property.Name), propertyNames);
                    var isRequired = required.Contains(property.Name);
                    var type = PropertyType(property.Value);
                    var description = property.Value.ValueKind == JsonValueKind.Object &&
                        property.Value.TryGetProperty("description", out var text) && text.ValueKind == JsonValueKind.String
                        ? text.GetString()! : property.Name;
                    AppendSummary(models, description, "    ");
                    models.AppendLine($"    [JsonPropertyName({Literal(property.Name)})]");
                    if (!isRequired)
                        models.AppendLine("    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]");
                    models.AppendLine($"    public {(isRequired ? "required " : "")}{type}{(isRequired ? "" : "?")} {propertyName} {{ get; init; }}");
                    models.AppendLine();
                }
            }

            models.AppendLine("""
                /// <summary>Additional wire arguments, including fields not described by the schema.</summary>
                [JsonExtensionData]
                public Dictionary<string, JsonElement>? ExtraArguments { get; init; }
            }

            """);
            context.AppendLine($"[JsonSerializable(typeof({model}))]");
        }

        // Tool names and schemas are data, never interpolated as executable C#.
        var source = $$"""
            // <auto-generated by mcp2lib />
            #nullable enable
            using global::System;
            using global::System.Collections.Generic;
            using global::System.Linq;
            using global::System.Text.Json;
            using global::System.Text.Json.Serialization;
            using global::System.Threading;
            using global::System.Threading.Tasks;
            using global::ModelContextProtocol.Client;
            using global::ModelContextProtocol.Protocol;

            namespace {{ns}};

            public sealed class {{className}} : IAsyncDisposable
            {
                private readonly McpClient _client;
                private readonly bool _ownsClient;

                /// <summary>Wrap an existing MCP session. The caller owns it unless ownsClient is true.</summary>
                public {{className}}(McpClient client, bool ownsClient = false)
                {
                    ArgumentNullException.ThrowIfNull(client);
                    _client = client;
                    _ownsClient = ownsClient;
                }

                /// <summary>Connect using an official SDK transport. Dispose the returned client when finished.</summary>
                public static async Task<{{className}}> ConnectAsync(
                    IClientTransport transport, McpClientOptions? options = null,
                    CancellationToken cancellationToken = default)
                {
                    var client = await McpClient.CreateAsync(transport, options, cancellationToken: cancellationToken);
                    return new {{className}}(client, ownsClient: true);
                }

                public ValueTask DisposeAsync() => _ownsClient ? _client.DisposeAsync() : ValueTask.CompletedTask;

            {{methods}}
            }

            {{models}}
            [JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
            [JsonSerializable(typeof(JsonElement))]
            {{context}}
            internal partial class JsonContext : JsonSerializerContext;
            """;

        var project = $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net11.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <IsAotCompatible>true</IsAotCompatible>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="ModelContextProtocol.Core" Version="{{McpPackageVersion}}" />
              </ItemGroup>
            </Project>
            """;

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"{className}.csproj"] = Normalize(project),
            [$"{className}.g.cs"] = Normalize(source),
            ["tools.json"] = Normalize(JsonSerializer.Serialize(
                tools.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray(), ToolJsonContext.Default.ToolArray))
        };
    }

    public static void WriteFiles(string directory, IReadOnlyDictionary<string, string> files, bool overwrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var fullPath = Path.GetFullPath(directory);
        foreach (var name in files.Keys)
        {
            if (Path.GetFileName(name) != name)
                throw new ArgumentException("Generated file names must not contain directories.");
            if (!overwrite && File.Exists(Path.Combine(fullPath, name)))
                throw new IOException($"'{Path.Combine(fullPath, name)}' already exists. Use --force to regenerate.");
        }
        Directory.CreateDirectory(fullPath);
        foreach (var (name, content) in files)
        {
            using var stream = new FileStream(Path.Combine(fullPath, name),
                overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(content);
        }
    }

    private static string PropertyType(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            schema.TryGetProperty("$ref", out _) ||
            schema.TryGetProperty("anyOf", out _) ||
            schema.TryGetProperty("oneOf", out _) ||
            schema.TryGetProperty("allOf", out _) ||
            !schema.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            return "JsonElement";

        return type.GetString() switch
        {
            "string" => "string",
            "integer" => "long",
            "number" => "double",
            "boolean" => "bool",
            "array" when schema.TryGetProperty("items", out var items) => PropertyType(items) + "[]",
            _ => "JsonElement"
        };
    }

    private static bool IsIdentifier(string value) =>
        !string.IsNullOrEmpty(value) && !Keywords.Contains(value) &&
        (char.IsAsciiLetter(value[0]) || value[0] == '_') &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static string Identifier(string value)
    {
        var result = new StringBuilder();
        var capitalize = true;
        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character))
            {
                capitalize = true;
                continue;
            }
            if (result.Length == 0 && char.IsAsciiDigit(character))
                result.Append('_');
            result.Append(capitalize ? char.ToUpperInvariant(character) : character);
            capitalize = false;
        }
        return result.Length == 0 ? "Tool" : result.ToString();
    }

    private static string Unique(string name, HashSet<string> used)
    {
        var candidate = name;
        for (var suffix = 2; !used.Add(candidate); suffix++)
            candidate = name + suffix.ToString(CultureInfo.InvariantCulture);
        return candidate;
    }

    private static string Literal(string value) => JsonSerializer.Serialize(value, ToolJsonContext.Default.String);

    private static void AppendSummary(StringBuilder builder, string text, string indent)
    {
        builder.AppendLine(indent + "/// <summary>");
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').Replace('\u0085', '\n').Replace('\u2028', '\n').Replace('\u2029', '\n').Split('\n'))
        {
            var escaped = line.Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
            builder.AppendLine((indent + "/// " + escaped).TrimEnd());
        }
        builder.AppendLine(indent + "/// </summary>");
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";
}
