using System.Buffers;
using System.Net;
using System.Net.Sockets;

namespace OyiEditor.Protocol.Tests;

/// <summary>A scripted loopback server standing in for <c>oyi serve --rpc</c>.</summary>
internal sealed class FakeEngine : IDisposable
{
    public static readonly ServerWelcome DefaultWelcome = new(RpcClient.ProtocolVersion, 0x5, "0.1.0-test", 0xABCDEF, 1);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public FakeEngine() => _listener.Start();

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public async Task<FakeSession> AcceptAsync() =>
        new(await _listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken));

    public void Dispose() => _listener.Dispose();
}

internal sealed class FakeSession : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly FrameReader _reader = new();

    public FakeSession(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    /// <summary>Reads the Hello and answers with <paramref name="welcome"/> (default: <see cref="FakeEngine.DefaultWelcome"/>).</summary>
    public async Task<Incoming.HelloMessage> HandshakeAsync(ServerWelcome? welcome = null)
    {
        var hello = Assert.IsType<Incoming.HelloMessage>(await ReceiveAsync());
        await SendAsync(EnvelopeCodec.EncodeWelcome(welcome ?? FakeEngine.DefaultWelcome));
        return hello;
    }

    public async Task<Incoming.RequestMessage> ReceiveRequestAsync() =>
        Assert.IsType<Incoming.RequestMessage>(await ReceiveAsync());

    public async Task<Incoming> ReceiveAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Timeout);
        var buffer = new byte[4096];
        while (!_reader.HasFrame)
        {
            var read = await _stream.ReadAsync(buffer, timeout.Token);
            if (read == 0)
            {
                throw new IOException("Client closed the connection.");
            }

            _reader.Push(buffer.AsSpan(0, read));
        }

        var message = EnvelopeCodec.Decode(_reader.PeekFrame());
        _reader.PopFrame();
        return message;
    }

    /// <summary>Waits until the client closes its side.</summary>
    public async Task<bool> WaitForCloseAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Timeout);
        var buffer = new byte[4096];
        try
        {
            while (await _stream.ReadAsync(buffer, timeout.Token) > 0)
            {
            }

            return true;
        }
        catch (IOException)
        {
            return true;
        }
    }

    public Task SendAsync(byte[] envelope)
    {
        var writer = new ArrayBufferWriter<byte>();
        Framing.AppendFrame(writer, envelope);
        return SendRawAsync(writer.WrittenSpan.ToArray());
    }

    public Task SendResponseAsync(uint id, RpcReply reply) => SendAsync(EnvelopeCodec.EncodeResponse(id, reply));

    public async Task SendRawAsync(byte[] bytes) =>
        await _stream.WriteAsync(bytes, TestContext.Current.CancellationToken);

    public void Dispose() => _client.Dispose();
}
