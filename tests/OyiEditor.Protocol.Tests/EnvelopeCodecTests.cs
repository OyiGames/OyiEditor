namespace OyiEditor.Protocol.Tests;

public sealed class EnvelopeCodecTests
{
    [Fact]
    public void RequestRoundTripsWithArgsAndPayload()
    {
        var bytes = EnvelopeCodec.EncodeRequest(7, "echo", ["a", "", "c"], [1, 2, 3]);

        var request = Assert.IsType<Incoming.RequestMessage>(EnvelopeCodec.Decode(bytes));
        Assert.Equal(7u, request.Id);
        Assert.Equal("echo", request.Command);
        Assert.Equal(["a", "", "c"], request.Args);
        Assert.Equal(new byte[] { 1, 2, 3 }, request.Payload.ToArray());
    }

    [Fact]
    public void EnvelopesCarryTheOyipFileIdentifier()
    {
        var bytes = EnvelopeCodec.EncodeHello(RpcClient.ProtocolVersion, 0, string.Empty, "x");

        Assert.Equal("OYIP"u8.ToArray(), bytes.AsSpan(4, 4).ToArray());
    }

    [Fact]
    public void WelcomeKeepsTheFullSchemaHash()
    {
        var welcome = FakeEngine.DefaultWelcome with { SchemaHash = 0x1973b126dda53b07 };

        var decoded = Assert.IsType<Incoming.WelcomeMessage>(EnvelopeCodec.Decode(EnvelopeCodec.EncodeWelcome(welcome)));
        Assert.Equal(welcome, decoded.Welcome);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F, (byte)'O', (byte)'Y', (byte)'I', (byte)'P' })]
    public void GarbageIsAProtocolError(byte[] bytes) =>
        Assert.Throws<ProtocolException>(() => EnvelopeCodec.Decode(bytes));

    [Fact]
    public void WrongFileIdentifierIsAProtocolError()
    {
        var bytes = EnvelopeCodec.EncodeHello(RpcClient.ProtocolVersion, 0, string.Empty, "x");
        bytes[4] = (byte)'X';

        Assert.Throws<ProtocolException>(() => EnvelopeCodec.Decode(bytes));
    }
}
