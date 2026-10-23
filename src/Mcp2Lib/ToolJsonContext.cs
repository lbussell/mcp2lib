using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;

namespace Mcp2Lib;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Tool[]))]
[JsonSerializable(typeof(string))]
internal partial class ToolJsonContext : JsonSerializerContext;
