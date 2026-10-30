using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Protocol;

namespace Proxus.Mcp;

/// <summary>Turns a tool's result record into an MCP result, flagging failures so the client sees isError.</summary>
internal static class ToolOutput
{
    public static CallToolResult Ok<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        return Create(value, typeInfo, isError: false);
    }

    public static CallToolResult Failed<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        return Create(value, typeInfo, isError: true);
    }

    private static CallToolResult Create<T>(T value, JsonTypeInfo<T> typeInfo, bool isError)
    {
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, typeInfo) }],
            IsError = isError
        };
    }
}

// camelCase, like the tool arguments the MCP SDK derives from parameter names.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<DeviceSummary>))]
[JsonSerializable(typeof(ReadResult))]
[JsonSerializable(typeof(DecodeResult))]
[JsonSerializable(typeof(BrowseResult))]
[JsonSerializable(typeof(ReadNodesResult))]
[JsonSerializable(typeof(DiagnoseResult))]
internal sealed partial class ToolJsonContext : JsonSerializerContext
{
    /// <summary>Unescaped output: device names and units such as °C stay readable for the model.</summary>
    public static ToolJsonContext Readable { get; } = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
}
