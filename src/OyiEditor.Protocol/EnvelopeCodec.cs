using Google.FlatBuffers;
using Wire = oyi.protocol;

namespace OyiEditor.Protocol;

/// <summary>A decoded envelope. The client only expects Welcome, Response and StreamFrame; the rest serve tests.</summary>
internal abstract record Incoming
{
    internal sealed record HelloMessage(uint ProtocolVersion, ulong Capabilities, string Auth, string ClientName)
        : Incoming;

    internal sealed record WelcomeMessage(ServerWelcome Welcome) : Incoming;

    internal sealed record RequestMessage(uint Id, string Command, IReadOnlyList<string> Args, ReadOnlyMemory<byte> Payload)
        : Incoming;

    internal sealed record ResponseMessage(uint Id, RpcReply Reply) : Incoming;

    internal sealed record StreamFrameMessage(StreamMessage Frame) : Incoming;
}

/// <summary>Builds and parses <c>Envelope</c> buffers (schema <c>protocol/schema/oyi_protocol.fbs</c>).</summary>
internal static class EnvelopeCodec
{
    public static byte[] EncodeHello(uint protocolVersion, ulong capabilities, string auth, string clientName)
    {
        var builder = new FlatBufferBuilder(128);
        var authOffset = builder.CreateString(auth);
        var nameOffset = builder.CreateString(clientName);
        var hello = Wire.Hello.CreateHello(builder, protocolVersion, capabilities, authOffset, nameOffset);
        return Finish(builder, Wire.Channel.Command, Wire.Message.Hello, hello.Value);
    }

    public static byte[] EncodeWelcome(ServerWelcome welcome)
    {
        var builder = new FlatBufferBuilder(128);
        var engineVersion = builder.CreateString(welcome.EngineVersion);
        var offset = Wire.Welcome.CreateWelcome(
            builder,
            welcome.ProtocolVersion,
            welcome.Capabilities,
            engineVersion,
            welcome.SchemaHash,
            welcome.SessionId);
        return Finish(builder, Wire.Channel.Command, Wire.Message.Welcome, offset.Value);
    }

    public static byte[] EncodeRequest(uint id, string command, IReadOnlyList<string> args, ReadOnlySpan<byte> payload)
    {
        var builder = new FlatBufferBuilder(128 + payload.Length);
        var commandOffset = builder.CreateString(command);
        var argsOffset = default(VectorOffset);
        if (args.Count > 0)
        {
            var strings = new StringOffset[args.Count];
            for (var i = 0; i < args.Count; ++i)
            {
                strings[i] = builder.CreateString(args[i]);
            }

            argsOffset = Wire.Request.CreateArgsVector(builder, strings);
        }

        var payloadOffset = payload.IsEmpty ? default : Wire.Request.CreatePayloadVectorBlock(builder, payload.ToArray());
        Wire.Request.StartRequest(builder);
        Wire.Request.AddId(builder, id);
        Wire.Request.AddCommand(builder, commandOffset);
        if (args.Count > 0)
        {
            Wire.Request.AddArgs(builder, argsOffset);
        }

        if (!payload.IsEmpty)
        {
            Wire.Request.AddPayload(builder, payloadOffset);
        }

        var request = Wire.Request.EndRequest(builder);
        return Finish(builder, Wire.Channel.Command, Wire.Message.Request, request.Value);
    }

    public static byte[] EncodeResponse(uint id, RpcReply reply)
    {
        var builder = new FlatBufferBuilder(128 + reply.Payload.Length);
        var errorCode = builder.CreateString(reply.ErrorCode);
        var message = builder.CreateString(reply.Message);
        var payload = reply.Payload.IsEmpty
            ? default
            : Wire.Response.CreatePayloadVectorBlock(builder, reply.Payload.ToArray());
        var response = Wire.Response.CreateResponse(builder, id, reply.Ok, errorCode, message, payload);
        return Finish(builder, Wire.Channel.Command, Wire.Message.Response, response.Value);
    }

    public static byte[] EncodeStreamFrame(StreamMessage frame)
    {
        var builder = new FlatBufferBuilder(128 + frame.Payload.Length);
        var stream = builder.CreateString(frame.Stream);
        var payload = frame.Payload.IsEmpty
            ? default
            : Wire.StreamFrame.CreatePayloadVectorBlock(builder, frame.Payload.ToArray());
        var offset = Wire.StreamFrame.CreateStreamFrame(builder, stream, frame.Sequence, payload);
        return Finish(builder, Wire.Channel.Stream, Wire.Message.StreamFrame, offset.Value);
    }

    /// <summary>Verifies and decodes one frame payload.</summary>
    /// <exception cref="ProtocolException">The bytes are not a valid <c>OYIP</c> envelope.</exception>
    public static Incoming Decode(ReadOnlySpan<byte> frame)
    {
        var buffer = new ByteBuffer(frame.ToArray());
        // VerifyEnvelope also checks the OYIP identifier. Upstream 25.12.19 skipped that check; the runtime vendored
        // from OyiGames/flatbuffers (v25.12.19-oyi.1) fixes it, and EnvelopeCodecTests guards against a regression.
        if (frame.Length < Framing.HeaderSize * 2 || !Wire.Envelope.VerifyEnvelope(buffer))
        {
            throw new ProtocolException($"Received {frame.Length} bytes that are not a valid OYIP envelope.");
        }

        var envelope = Wire.Envelope.GetRootAsEnvelope(buffer);
        switch (envelope.MessageType)
        {
            case Wire.Message.Hello:
                {
                    var hello = envelope.MessageAsHello();
                    return new Incoming.HelloMessage(
                        hello.ProtocolVersion,
                        hello.Capabilities,
                        hello.Auth ?? string.Empty,
                        hello.ClientName ?? string.Empty);
                }

            case Wire.Message.Welcome:
                {
                    var welcome = envelope.MessageAsWelcome();
                    return new Incoming.WelcomeMessage(new ServerWelcome(
                        welcome.ProtocolVersion,
                        welcome.Capabilities,
                        welcome.EngineVersion ?? string.Empty,
                        welcome.SchemaHash,
                        welcome.SessionId));
                }

            case Wire.Message.Request:
                {
                    var request = envelope.MessageAsRequest();
                    var args = new string[request.ArgsLength];
                    for (var i = 0; i < args.Length; ++i)
                    {
                        args[i] = request.Args(i) ?? string.Empty;
                    }

                    return new Incoming.RequestMessage(
                        request.Id,
                        request.Command ?? string.Empty,
                        args,
                        request.GetPayloadArray() ?? []);
                }

            case Wire.Message.Response:
                {
                    var response = envelope.MessageAsResponse();
                    return new Incoming.ResponseMessage(
                        response.Id,
                        new RpcReply(
                            response.Ok,
                            response.ErrorCode ?? string.Empty,
                            response.Message ?? string.Empty,
                            response.GetPayloadArray() ?? []));
                }

            case Wire.Message.StreamFrame:
                {
                    var frameMessage = envelope.MessageAsStreamFrame();
                    return new Incoming.StreamFrameMessage(new StreamMessage(
                        frameMessage.Stream ?? string.Empty,
                        frameMessage.Sequence,
                        frameMessage.GetPayloadArray() ?? []));
                }

            default:
                throw new ProtocolException($"Envelope carries no known message (type {envelope.MessageType}).");
        }
    }

    private static byte[] Finish(FlatBufferBuilder builder, Wire.Channel channel, Wire.Message type, int messageOffset)
    {
        var envelope = Wire.Envelope.CreateEnvelope(builder, channel, type, messageOffset);
        Wire.Envelope.FinishEnvelopeBuffer(builder, envelope);
        return builder.SizedByteArray();
    }
}
