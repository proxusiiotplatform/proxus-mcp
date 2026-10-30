using Proxus.Drivers;
using Proxus.Mcp;

namespace Proxus.Mcp.Tests;

public class NodeIdTextTests
{
    [Theory]
    [InlineData("i=2258", "ns=0;i=2258")]
    [InlineData("ns=2;i=5", "ns=2;i=5")]
    [InlineData("ns=2;s=Line1.Temperature", "ns=2;s=Line1.Temperature")]
    [InlineData(" ns=3;s=A;B ", "ns=3;s=A;B")]
    [InlineData("ns=1;g=72962b91-fa75-4ae6-8d28-b404dc7daf63", "ns=1;g=72962b91-fa75-4ae6-8d28-b404dc7daf63")]
    [InlineData("ns=4;b=AQID", "ns=4;b=AQID")]
    public void Parses_the_standard_text_forms(string text, string expected)
    {
        Assert.True(NodeIdText.TryParse(text, out var nodeId, out var error), error);
        Assert.Equal(expected, nodeId.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("ns=x;i=1")]
    [InlineData("ns=2")]
    [InlineData("i=abc")]
    [InlineData("q=1")]
    [InlineData("ns=1;s=")]
    [InlineData("ns=1;g=not-a-guid")]
    [InlineData("ns=1;b=***")]
    public void Rejects_malformed_text_with_a_message(string text)
    {
        Assert.False(NodeIdText.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Namespace_uris_are_refused_explicitly()
    {
        Assert.False(NodeIdText.TryParse("nsu=urn:x;s=a", out _, out var error));
        Assert.Contains("nsu=", error);
    }

    [Theory]
    [InlineData("nsu=urn:example:plant;s=Line1.Temp", "urn:example:plant", "ns=0;s=Line1.Temp")]
    [InlineData("nsu=http://www.prosysopc.com/OPCUA/SimulationNodes/;i=1001", "http://www.prosysopc.com/OPCUA/SimulationNodes/", "ns=0;i=1001")]
    [InlineData("nsu=urn:x;s=A;B", "urn:x", "ns=0;s=A;B")]
    [InlineData("i=2258", null, "ns=0;i=2258")]
    public void Namespace_uri_form_returns_the_uri_for_the_server_to_resolve(string text, string? uri, string expected)
    {
        Assert.True(NodeIdText.TryParse(text, out var nodeId, out var namespaceUri, out var error), error);
        Assert.Equal(uri, namespaceUri);
        Assert.Equal(expected, nodeId.ToString());
    }

    [Theory]
    [InlineData("nsu=urn:x")]
    [InlineData("nsu=;s=a")]
    [InlineData("nsu=urn:x;q=1")]
    public void Malformed_namespace_uri_form_is_rejected(string text)
    {
        Assert.False(NodeIdText.TryParse(text, out _, out _, out var error));
        Assert.Contains("nsu=", error);
    }

    [Theory]
    [InlineData("i=1001", "ns=3;i=1001")]
    [InlineData("s=Line1", "ns=3;s=Line1")]
    [InlineData("g=72962b91-fa75-4ae6-8d28-b404dc7daf63", "ns=3;g=72962b91-fa75-4ae6-8d28-b404dc7daf63")]
    [InlineData("b=AQID", "ns=3;b=AQID")]
    public void WithNamespace_keeps_the_identifier(string text, string expected)
    {
        Assert.True(NodeIdText.TryParse(text, out var nodeId, out _));
        Assert.Equal(expected, NodeIdText.WithNamespace(nodeId, 3).ToString());
    }

    [Fact]
    public void Numeric_identifier_keeps_its_kind()
    {
        Assert.True(NodeIdText.TryParse("ns=2;i=42", out var nodeId, out _));
        Assert.Equal(IndustrialNodeIdKind.Numeric, nodeId.Kind);
        Assert.Equal(42UL, nodeId.NumericIdentifier);
    }
}
