using System.Text;
using OyiEditor.App.Console;
using OyiEditor.Protocol;

namespace OyiEditor.App.Tests;

public sealed class CommandInputTests
{
    [Fact]
    public void CommandAndArgsSplitOnWhitespace()
    {
        Assert.True(CommandInput.TryParse("  sched   validate\tfoo ", out var input));

        Assert.Equal("sched", input.Command);
        Assert.Equal(["validate", "foo"], input.Args);
        Assert.Empty(input.Payload);
    }

    [Theory]
    [InlineData("echo -- hello world", "hello world")]
    [InlineData("echo --  two spaces", " two spaces")]
    [InlineData("echo --", "")]
    [InlineData("echo -- a -- b", "a -- b")]
    public void TextAfterAStandaloneDoubleDashIsThePayload(string line, string payload)
    {
        Assert.True(CommandInput.TryParse(line, out var input));

        Assert.Equal("echo", input.Command);
        Assert.Empty(input.Args);
        Assert.Equal(payload, Encoding.UTF8.GetString(input.Payload));
    }

    [Fact]
    public void DoubleDashInsideAWordIsAnArgument()
    {
        Assert.True(CommandInput.TryParse("run --fast x--y", out var input));

        Assert.Equal(["--fast", "x--y"], input.Args);
        Assert.Empty(input.Payload);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-- only payload")]
    public void LinesWithoutACommandAreRejected(string line) => Assert.False(CommandInput.TryParse(line, out _));
}

public sealed class ReplyFormatTests
{
    [Fact]
    public void SuccessShowsMessageAndTextPayload()
    {
        var lines = ReplyFormat.Describe(new RpcReply(true, string.Empty, "pong", "{\"engine\":\"0.1.0\"}"u8.ToArray()));

        Assert.Equal(["ok: pong", "{\"engine\":\"0.1.0\"}"], lines);
    }

    [Fact]
    public void FailureShowsCodeAndMessage()
    {
        var lines = ReplyFormat.Describe(new RpcReply(false, "NotFound", "unknown command", ReadOnlyMemory<byte>.Empty));

        Assert.Equal(["error NotFound: unknown command"], lines);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x00 })]
    [InlineData(new byte[] { (byte)'a', 0x01, (byte)'b' })]
    public void BinaryPayloadIsShownAsItsSize(byte[] payload) =>
        Assert.Equal($"<{payload.Length} bytes>", ReplyFormat.Payload(payload));

    [Fact]
    public void StreamFrameShowsStreamSequenceAndPayload() =>
        Assert.Equal("[log #7] hello", ReplyFormat.Describe(new StreamMessage("log", 7, "hello"u8.ToArray())));
}
