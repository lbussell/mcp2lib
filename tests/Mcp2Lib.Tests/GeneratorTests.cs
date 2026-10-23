using System.Text.Json;
using Mcp2Lib;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Mcp2Lib.Tests;

public sealed class GeneratorTests
{
    [Fact]
    public void GeneratesTypedInputsWithoutReflection()
    {
        var files = LibraryGenerator.Generate([Tool("read", """
            {"type":"object","properties":{
              "name":{"type":"string"},"count":{"type":"integer"},"ratio":{"type":"number"},
              "enabled":{"type":"boolean"},"tags":{"type":"array","items":{"type":"string"}},
              "object":{"type":"object"},"union":{"anyOf":[{"type":"string"},{"type":"null"}]},
              "reference":{"$ref":"#/$defs/example"}
            },"required":["name"]}
            """)], "Example", "Tools");
        var code = files["Tools.g.cs"];
        Assert.Contains("public required string Name", code);
        Assert.Contains("public long? Count", code);
        Assert.Contains("public double? Ratio", code);
        Assert.Contains("public bool? Enabled", code);
        Assert.Contains("public string[]? Tags", code);
        Assert.Contains("public JsonElement? Object2", code);
        Assert.Contains("public JsonElement? Union", code);
        Assert.Contains("public JsonElement? Reference", code);
        Assert.Contains("JsonIgnoreCondition.WhenWritingNull", code);
        Assert.Contains("JsonContext.Default.ReadArguments", code);
        Assert.Contains("<IsAotCompatible>true</IsAotCompatible>", files["Tools.csproj"]);
    }

    [Theory]
    [InlineData("", "Tools")]
    [InlineData("bad-name", "Tools")]
    [InlineData("class", "Tools")]
    [InlineData("Example", "../Tools")]
    [InlineData("Example", "JsonContext")]
    [InlineData("Example", "class")]
    public void RejectsInvalidNames(string ns, string className) =>
        Assert.Throws<ArgumentException>(() => LibraryGenerator.Generate([], ns, className));

    [Fact]
    public void NamesAreDeterministicAndCollisionsAreDisambiguated()
    {
        var tools = new[] { Tool("a-b"), Tool("a_b"), Tool("connect"), Tool("123"), Tool("!!!") };
        var first = LibraryGenerator.Generate(tools, "Example", "Tools");
        var second = LibraryGenerator.Generate(tools.Reverse().ToArray(), "Example", "Tools");
        Assert.Equal(first["Tools.g.cs"], second["Tools.g.cs"]);
        Assert.Equal(first["tools.json"], second["tools.json"]);
        Assert.Contains("ABAsync(", first["Tools.g.cs"]);
        Assert.Contains("ABAsync2(", first["Tools.g.cs"]);
        Assert.Contains("ConnectAsync2(", first["Tools.g.cs"]);
        Assert.Contains("_123Async(", first["Tools.g.cs"]);
    }

    [Fact]
    public void EscapesWireNamesAndDocumentation()
    {
        var tool = Tool("quote\"\\\n", """{"type":"object","properties":{"quote\"\\":{"type":"string"}}}""");
        tool.Description = "</summary>\n<code>& hello  \n";
        var files = LibraryGenerator.Generate([tool], "Example", "Tools");
        var code = files["Tools.g.cs"];
        Assert.Contains("&lt;/summary&gt;", code);
        Assert.Contains("/// &lt;code&gt;&amp; hello", code);
        Assert.All(code.Split('\n'), line => Assert.Equal(line.TrimEnd(), line));
        Assert.Contains("""Name = "quote\u0022\\\n";""".Replace(";", ","), code);
        Assert.Equal(tool.Name, JsonDocument.Parse(files["tools.json"]).RootElement[0].GetProperty("name").GetString());
    }

    [Fact]
    public void HandlesEmptyToolList() =>
        Assert.Contains("[JsonSerializable(typeof(JsonElement))]",
            LibraryGenerator.Generate([], "Example", "Tools")["Tools.g.cs"]);

    [Fact]
    public void RejectsDuplicateToolNames() =>
        Assert.Throws<ArgumentException>(() => LibraryGenerator.Generate([Tool("same"), Tool("same")], "Example", "Tools"));

    [Fact]
    public void RefusesOverwriteBeforeWritingAndPreservesUnrelatedFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mcp2lib-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "tools.json"), "original");
            File.WriteAllText(Path.Combine(directory, "keep.txt"), "keep");
            var files = LibraryGenerator.Generate([], "Example", "Tools");
            Assert.Throws<IOException>(() => LibraryGenerator.WriteFiles(directory, files, false));
            Assert.False(File.Exists(Path.Combine(directory, "Tools.csproj")));
            Assert.Equal("original", File.ReadAllText(Path.Combine(directory, "tools.json")));
            LibraryGenerator.WriteFiles(directory, files, true);
            Assert.Equal("keep", File.ReadAllText(Path.Combine(directory, "keep.txt")));
            Assert.Equal(files["Tools.g.cs"], File.ReadAllText(Path.Combine(directory, "Tools.g.cs")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static Tool Tool(string name, string schema = """{"type":"object"}""") =>
        new() { Name = name, InputSchema = JsonDocument.Parse(schema).RootElement.Clone() };
}
