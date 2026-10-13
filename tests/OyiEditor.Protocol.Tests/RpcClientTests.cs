using System.Net.Sockets;
using System.Text;

namespace OyiEditor.Protocol.Tests;

public sealed class RpcClientTests
{
    private static readonly RpcReply Pong = new(true, string.Empty, "pong", ReadOnlyMemory<byte>.Empty);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HandshakeSendsHelloAndExposesWelcome()
    {
        using var engine = new FakeEngine();
        var accepted = engine.AcceptAsync();
        var connecting = RpcClient.ConnectAsync(
            "127.0.0.1",
            engine.Port,
            new RpcClientOptions { ClientName = "tests", Capabilities = 0x3 },
            Token);
        using var session = await accepted;
        var hello = await session.HandshakeAsync();
        await using var client = await connecting;

        Assert.Equal(RpcClient.ProtocolVersion, hello.ProtocolVersion);
        Assert.Equal(0x3UL, hello.Capabilities);
        Assert.Equal(string.Empty, hello.Auth);
        Assert.Equal("tests", hello.ClientName);
        Assert.Equal(FakeEngine.DefaultWelcome, client.Welcome);
    }

    [Fact]
    public async Task RefusedHelloThrowsRejectedWithTheEngineErrorCode()
    {
        using var engine = new FakeEngine();
        var accepted = engine.AcceptAsync();
        var connecting = RpcClient.ConnectAsync("127.0.0.1", engine.Port, cancellationToken: Token);
        using var session = await accepted;
        Assert.IsType<Incoming.HelloMessage>(await session.ReceiveAsync());
        await session.SendResponseAsync(0, new RpcReply(false, "Unsupported", "protocol 1 only", ReadOnlyMemory<byte>.Empty));
        session.Dispose();

        var error = await Assert.ThrowsAsync<RpcRejectedException>(() => connecting);
        Assert.Equal("Unsupported", error.ErrorCode);
        Assert.Equal("protocol 1 only", error.Message);
    }

    [Fact]
    public async Task WelcomeWithAnotherProtocolVersionIsRejected()
    {
        using var engine = new FakeEngine();
        var accepted = engine.AcceptAsync();
        var connecting = RpcClient.ConnectAsync("127.0.0.1", engine.Port, cancellationToken: Token);
        using var session = await accepted;
        await session.HandshakeAsync(FakeEngine.DefaultWelcome with { ProtocolVersion = 2 });

        var error = await Assert.ThrowsAsync<RpcRejectedException>(() => connecting);
        Assert.Equal("Unsupported", error.ErrorCode);
        Assert.True(await session.WaitForCloseAsync());
    }

    [Fact]
    public async Task EngineHangingUpDuringHandshakeIsAnIoError()
    {
        using var engine = new FakeEngine();
        var accepted = engine.AcceptAsync();
        var connecting = RpcClient.ConnectAsync("127.0.0.1", engine.Port, cancellationToken: Token);
        using (var session = await accepted)
        {
            Assert.IsType<Incoming.HelloMessage>(await session.ReceiveAsync());
        }

        await Assert.ThrowsAsync<IOException>(() => connecting);
    }

    [Fact]
    public async Task SilentEngineTimesOut()
    {
        using var engine = new FakeEngine();
        var accepted = engine.AcceptAsync();
        var options = new RpcClientOptions { HandshakeTimeout = TimeSpan.FromMilliseconds(200) };
        var connecting = RpcClient.ConnectAsync("127.0.0.1", engine.Port, options, Token);
        using var session = await accepted;

        await Assert.ThrowsAsync<TimeoutException>(() => connecting);
    }

    [Fact]
    public async Task NothingListeningIsASocketError()
    {
        int port;
        using (var engine = new FakeEngine())
        {
            port = engine.Port;
        }

        await Assert.ThrowsAnyAsync<SocketException>(() => RpcClient.ConnectAsync("127.0.0.1", port, cancellationToken: Token));
    }

    [Fact]
    public async Task OverlappingCallsAreMatchedByIdEvenWhenAnsweredOutOfOrder()
    {
        await using var connected = await ConnectedAsync();
        var (session, client) = (connected.Session, connected.Client);

        var first = client.CallAsync("echo", ["a", "b"], "abc"u8.ToArray(), Token);
        var second = client.CallAsync("ping", cancellationToken: Token);
        var firstRequest = await session.ReceiveRequestAsync();
        var secondRequest = await session.ReceiveRequestAsync();

        Assert.Equal("echo", firstRequest.Command);
        Assert.Equal(["a", "b"], firstRequest.Args);
        Assert.Equal("abc"u8.ToArray(), firstRequest.Payload.ToArray());
        Assert.Equal("ping", secondRequest.Command);
        Assert.Empty(secondRequest.Args);
        Assert.True(secondRequest.Payload.IsEmpty);
        Assert.NotEqual(firstRequest.Id, secondRequest.Id);

        await session.SendResponseAsync(secondRequest.Id, Pong);
        await session.SendResponseAsync(firstRequest.Id, new RpcReply(true, string.Empty, "a,b", "cba"u8.ToArray()));

        var secondReply = await second;
        var firstReply = await first;
        Assert.Equal("pong", secondReply.Message);
        Assert.Equal("a,b", firstReply.Message);
        Assert.Equal("cba", Encoding.UTF8.GetString(firstReply.Payload.Span));
    }

    [Fact]
    public async Task CommandFailureIsAReplyNotAnException()
    {
        await using var connected = await ConnectedAsync();
        var (session, client) = (connected.Session, connected.Client);

        var call = client.CallAsync("nope", cancellationToken: Token);
        var request = await session.ReceiveRequestAsync();
        await session.SendResponseAsync(request.Id, new RpcReply(false, "NotFound", "unknown command", ReadOnlyMemory<byte>.Empty));

        var reply = await call;
        Assert.False(reply.Ok);
        Assert.Equal("NotFound", reply.ErrorCode);
        Assert.Equal("unknown command", reply.Message);
    }

    [Fact]
    public async Task StreamFramesArriveInOrderWhileACallIsWaiting()
    {
        await using var connected = await ConnectedAsync();
        var (session, client) = (connected.Session, connected.Client);

        var call = client.CallAsync("ping", cancellationToken: Token);
        var request = await session.ReceiveRequestAsync();
        await session.SendAsync(EnvelopeCodec.EncodeStreamFrame(new StreamMessage("log", 1, "one"u8.ToArray())));
        await session.SendAsync(EnvelopeCodec.EncodeStreamFrame(new StreamMessage("log", 2, "two"u8.ToArray())));
        await session.SendResponseAsync(request.Id, Pong);

        Assert.Equal("pong", (await call).Message);
        var one = await client.Streams.ReadAsync(Token);
        var two = await client.Streams.ReadAsync(Token);
        Assert.Equal(("log", 1UL, "one"), (one.Stream, one.Sequence, Encoding.UTF8.GetString(one.Payload.Span)));
        Assert.Equal(("log", 2UL, "two"), (two.Stream, two.Sequence, Encoding.UTF8.GetString(two.Payload.Span)));
    }

    [Fact]
    public async Task StreamOverflowDropsTheOldestFrames()
    {
        await using var connected = await ConnectedAsync(new RpcClientOptions { StreamCapacity = 2 });
        var (session, client) = (connected.Session, connected.Client);

        for (var sequence = 1UL; sequence <= 4; ++sequence)
        {
            await session.SendAsync(EnvelopeCodec.EncodeStreamFrame(new StreamMessage("s", sequence, ReadOnlyMemory<byte>.Empty)));
        }

        // A reply after the frames proves they have all been dispatched.
        var call = client.CallAsync("ping", cancellationToken: Token);
        await session.SendResponseAsync((await session.ReceiveRequestAsync()).Id, Pong);
        await call;

        Assert.Equal(3UL, (await client.Streams.ReadAsync(Token)).Sequence);
        Assert.Equal(4UL, (await client.Streams.ReadAsync(Token)).Sequence);
        Assert.False(client.Streams.TryRead(out _));
    }

    [Fact]
    public async Task CancelledCallStopsWaitingAndItsLateResponseIsIgnored()
    {
        await using var connected = await ConnectedAsync();
        var (session, client) = (connected.Session, connected.Client);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var abandoned = client.CallAsync("slow", cancellationToken: cancel.Token);
        var slowRequest = await session.ReceiveRequestAsync();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        await session.SendResponseAsync(slowRequest.Id, Pong);
        var next = client.CallAsync("ping", cancellationToken: Token);
        var nextRequest = await session.ReceiveRequestAsync();
        await session.SendResponseAsync(nextRequest.Id, new RpcReply(true, string.Empty, "second", ReadOnlyMemory<byte>.Empty));

        Assert.Equal("second", (await next).Message);
        Assert.False(client.Closed.IsCompleted);
    }

    [Fact]
    public async Task MalformedFrameClosesTheConnectionAndFailsPendingCalls()
    {
        await using var connected = await ConnectedAsync();
        var (session, client) = (connected.Session, connected.Client);

        var call = client.CallAsync("ping", cancellationToken: Token);
        await session.ReceiveRequestAsync();
        await session.SendAsync([1, 2, 3, 4, 5, 6, 7, 8, 9]);

        await Assert.ThrowsAsync<ProtocolException>(() => call);
        Assert.IsType<ProtocolException>(await client.Closed);
        Assert.True(await session.WaitForCloseAsync());
        await Assert.ThrowsAsync<ProtocolException>(() => client.CallAsync("ping", cancellationToken: Token));
    }

    [Fact]
    public async Task OversizeFrameHeaderClosesTheConnection()
    {
        await using var connected = await ConnectedAsync();
        var (session, client) = (connected.Session, connected.Client);

        await session.SendRawAsync([0x01, 0x00, 0x00, 0x01]);

        Assert.IsType<ProtocolException>(await client.Closed);
    }

    [Fact]
    public async Task EngineHangingUpFailsPendingCallsAndClosesCleanly()
    {
        await using var connected = await ConnectedAsync();
        var (session, client) = (connected.Session, connected.Client);

        var call = client.CallAsync("ping", cancellationToken: Token);
        await session.ReceiveRequestAsync();
        session.Dispose();

        await Assert.ThrowsAsync<IOException>(() => call);
        Assert.Null(await client.Closed);
        Assert.False(await client.Streams.WaitToReadAsync(Token));
    }

    [Fact]
    public async Task DisposeFailsPendingCallsAndClosesTheSocket()
    {
        await using var connected = await ConnectedAsync();
        var (session, client) = (connected.Session, connected.Client);

        var call = client.CallAsync("ping", cancellationToken: Token);
        await session.ReceiveRequestAsync();
        await client.DisposeAsync();

        await Assert.ThrowsAsync<IOException>(() => call);
        Assert.Null(await client.Closed);
        Assert.True(await session.WaitForCloseAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.CallAsync("ping", cancellationToken: Token));
        await client.DisposeAsync(); // idempotent
    }

    private static async Task<Connected> ConnectedAsync(RpcClientOptions? options = null)
    {
        var engine = new FakeEngine();
        var accepted = engine.AcceptAsync();
        var connecting = RpcClient.ConnectAsync("127.0.0.1", engine.Port, options, Token);
        var session = await accepted;
        await session.HandshakeAsync();
        return new Connected(engine, session, await connecting);
    }

    private sealed record Connected(FakeEngine Engine, FakeSession Session, RpcClient Client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            Session.Dispose();
            Engine.Dispose();
        }
    }
}
