using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Channels;

namespace OyiEditor.Protocol;

/// <summary>Connection settings for <see cref="RpcClient.ConnectAsync"/>.</summary>
public sealed record RpcClientOptions
{
    /// <summary>Sent in Hello; shows up in engine logs.</summary>
    public string ClientName { get; init; } = "OyiEditor";

    /// <summary>One-time pairing token; loopback connections leave it empty.</summary>
    public string AuthToken { get; init; } = string.Empty;

    public ulong Capabilities { get; init; }

    /// <summary>Upper bound for connect + Hello + Welcome.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Stream frames kept for <see cref="RpcClient.Streams"/>; beyond this the oldest are dropped, which the stream
    /// channel allows. Responses are never dropped.
    /// </summary>
    public int StreamCapacity { get; init; } = 1024;
}

/// <summary>
/// Client side of <c>oyi serve --rpc</c>: one TCP connection, a Hello / Welcome handshake, then requests matched to
/// responses by id on the command channel and stream frames on <see cref="Streams"/>. Calls may overlap and may come
/// from any thread.
/// </summary>
public sealed class RpcClient : IAsyncDisposable
{
    /// <summary>The protocol version this build speaks (while OyiProtocol is 0.x, the tag MINOR: v0.N.x).</summary>
    public const uint ProtocolVersion = 1;

    private const int ReceiveBufferSize = 64 * 1024;

    private readonly NetworkStream _stream;
    private readonly FrameReader _reader;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<RpcReply>> _pending = new();
    private readonly Channel<StreamMessage> _streams;
    private readonly TaskCompletionSource<Exception?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _shutdown = new();
    private Task _receiveLoop = Task.CompletedTask;
    private int _nextId;
    private int _disposed;
    private volatile bool _terminated;

    private RpcClient(NetworkStream stream, FrameReader reader, ServerWelcome welcome, int streamCapacity)
    {
        _stream = stream;
        _reader = reader;
        Welcome = welcome;
        _streams = Channel.CreateBounded<StreamMessage>(new BoundedChannelOptions(Math.Max(1, streamCapacity))
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
        });
    }

    public ServerWelcome Welcome { get; }

    /// <summary>Stream frames in arrival order. Completes when the connection ends.</summary>
    public ChannelReader<StreamMessage> Streams => _streams.Reader;

    /// <summary>
    /// Completes when the connection ends: with <see langword="null"/> after the engine hung up or after
    /// <see cref="DisposeAsync"/>, otherwise with the error that ended it.
    /// </summary>
    public Task<Exception?> Closed => _closed.Task;

    /// <summary>Connects and performs the handshake.</summary>
    /// <exception cref="RpcRejectedException">The engine refused the Hello, or speaks another protocol version.</exception>
    /// <exception cref="TimeoutException">No Welcome within <see cref="RpcClientOptions.HandshakeTimeout"/>.</exception>
    /// <exception cref="SocketException">Nothing is listening, or the host cannot be resolved.</exception>
    /// <exception cref="IOException">The engine closed the connection during the handshake.</exception>
    /// <exception cref="ProtocolException">The engine sent something other than Welcome or a refusal.</exception>
    public static async Task<RpcClient> ConnectAsync(
        string host,
        int port,
        RpcClientOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        options ??= new RpcClientOptions();

        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshake.CancelAfter(options.HandshakeTimeout);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        NetworkStream? stream = null;
        try
        {
            await socket.ConnectAsync(host, port, handshake.Token).ConfigureAwait(false);
            stream = new NetworkStream(socket, ownsSocket: true);
            var hello = EnvelopeCodec.EncodeHello(
                ProtocolVersion,
                options.Capabilities,
                options.AuthToken,
                options.ClientName);
            await stream.WriteAsync(ToFrame(hello), handshake.Token).ConfigureAwait(false);

            var reader = new FrameReader();
            var first = await ReceiveFirstFrameAsync(stream, reader, handshake.Token).ConfigureAwait(false);
            var welcome = first switch
            {
                Incoming.WelcomeMessage message => message.Welcome,
                Incoming.ResponseMessage { Reply.Ok: false } refusal =>
                    throw new RpcRejectedException(refusal.Reply.ErrorCode, refusal.Reply.Message),
                _ => throw new ProtocolException($"Expected Welcome as the first frame, got {first.GetType().Name}."),
            };
            if (welcome.ProtocolVersion != ProtocolVersion)
            {
                throw new RpcRejectedException(
                    "Unsupported",
                    $"The engine speaks protocol {welcome.ProtocolVersion}; this editor speaks {ProtocolVersion}.");
            }

            var client = new RpcClient(stream, reader, welcome, options.StreamCapacity);
            client._receiveLoop = client.ReceiveLoopAsync();
            return client;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Close(socket, stream);
            throw new TimeoutException(
                $"No handshake from {host}:{port} within {options.HandshakeTimeout.TotalSeconds:0.###} s.");
        }
        catch
        {
            Close(socket, stream);
            throw;
        }
    }

    /// <summary>Sends one request and waits for its response.</summary>
    /// <remarks>
    /// A command failure is a reply with <see cref="RpcReply.Ok"/> false, not an exception. Cancelling stops the
    /// wait; a late response for it is discarded.
    /// </remarks>
    /// <exception cref="IOException">The connection ended before the response arrived.</exception>
    /// <exception cref="ProtocolException">The engine broke the protocol; the connection is closed.</exception>
    public async Task<RpcReply> CallAsync(
        string command,
        IReadOnlyList<string>? args = null,
        ReadOnlyMemory<byte> payload = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(command);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var id = unchecked((uint)Interlocked.Increment(ref _nextId));
        var frame = ToFrame(EnvelopeCodec.EncodeRequest(id, command, args ?? [], payload.Span));
        var reply = new TaskCompletionSource<RpcReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        // Terminate() sets _terminated before draining _pending, so either it fails this entry or we see the flag.
        if (_terminated)
        {
            _pending.TryRemove(id, out _);
            throw await TerminalErrorAsync().ConfigureAwait(false);
        }

        await using var registration = cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(id, out var abandoned))
            {
                abandoned.TrySetCanceled(cancellationToken);
            }
        }).ConfigureAwait(false);

        try
        {
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Never cancel mid-frame: a half-written frame would desynchronize the stream for every other call.
                await _stream.WriteAsync(frame, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (Exception) when (_terminated && !cancellationToken.IsCancellationRequested)
        {
            // The connection ended while we were sending; report why instead of the disposed-stream error.
            _pending.TryRemove(id, out _);
            throw await TerminalErrorAsync().ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }

        return await reply.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
        await _receiveLoop.ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private static ReadOnlyMemory<byte> ToFrame(byte[] envelope)
    {
        var writer = new ArrayBufferWriter<byte>(Framing.HeaderSize + envelope.Length);
        Framing.AppendFrame(writer, envelope);
        return writer.WrittenMemory;
    }

    private static async Task<Incoming> ReceiveFirstFrameAsync(
        NetworkStream stream,
        FrameReader reader,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
        try
        {
            while (!reader.HasFrame)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("The engine closed the connection during the handshake.");
                }

                reader.Push(buffer.AsSpan(0, read));
            }

            var first = EnvelopeCodec.Decode(reader.PeekFrame());
            reader.PopFrame();
            return first;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void Close(Socket socket, NetworkStream? stream)
    {
        if (stream is not null)
        {
            stream.Dispose();
        }
        else
        {
            socket.Dispose();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        Exception? error = null;
        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
        try
        {
            DispatchBufferedFrames();
            while (true)
            {
                var read = await _stream.ReadAsync(buffer, _shutdown.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                _reader.Push(buffer.AsSpan(0, read));
                DispatchBufferedFrames();
            }
        }
        catch (Exception) when (_shutdown.IsCancellationRequested)
        {
            // DisposeAsync: the read was cancelled or the stream was closed under it.
        }
        catch (Exception exception)
        {
            error = exception;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            Terminate(error);
        }
    }

    private void DispatchBufferedFrames()
    {
        while (_reader.HasFrame)
        {
            var message = EnvelopeCodec.Decode(_reader.PeekFrame());
            _reader.PopFrame();
            switch (message)
            {
                case Incoming.ResponseMessage response:
                    // No entry means the call was cancelled; its late response is dropped.
                    if (_pending.TryRemove(response.Id, out var waiting))
                    {
                        waiting.TrySetResult(response.Reply);
                    }

                    break;
                case Incoming.StreamFrameMessage stream:
                    _streams.Writer.TryWrite(stream.Frame);
                    break;
                default:
                    throw new ProtocolException($"Unexpected {message.GetType().Name} from the engine.");
            }
        }
    }

    private void Terminate(Exception? error)
    {
        _terminated = true;
        _stream.Dispose();
        _streams.Writer.TryComplete();
        var failure = error ?? new IOException("The connection to the engine was closed.");
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var waiting))
            {
                waiting.TrySetException(failure);
            }
        }

        _closed.TrySetResult(error);
    }

    private async Task<Exception> TerminalErrorAsync() =>
        await _closed.Task.ConfigureAwait(false) ?? new IOException("The connection to the engine was closed.");
}
