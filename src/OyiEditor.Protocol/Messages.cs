namespace OyiEditor.Protocol;

/// <summary>What the engine reported when it accepted the handshake.</summary>
/// <param name="ProtocolVersion">The engine's protocol version; equal to <see cref="RpcClient.ProtocolVersion"/>.</param>
/// <param name="Capabilities">Capability bits the engine enabled for this session.</param>
/// <param name="EngineVersion">Engine version string, for display.</param>
/// <param name="SchemaHash">
/// Fingerprint of the engine build's reflected component layouts (the <c>schemaHash</c> of
/// <c>oyi --version --json</c>). Equal values mean component snapshots are binary compatible. The editor can only
/// compare it with a value it saw before; it cannot recompute it.
/// </param>
/// <param name="SessionId">Server-assigned id of this connection.</param>
public sealed record ServerWelcome(
    uint ProtocolVersion,
    ulong Capabilities,
    string EngineVersion,
    ulong SchemaHash,
    uint SessionId);

/// <summary>The engine's answer to one request.</summary>
/// <param name="Ok">False when the command failed; <see cref="ErrorCode"/> and <see cref="Message"/> say why.</param>
/// <param name="ErrorCode">Machine-readable failure code such as <c>NotFound</c>; empty on success.</param>
/// <param name="Message">Human-readable text; some commands also use it on success (for example <c>ping</c>).</param>
/// <param name="Payload">Command-specific bytes; empty when the engine sent none.</param>
public sealed record RpcReply(bool Ok, string ErrorCode, string Message, ReadOnlyMemory<byte> Payload);

/// <summary>One frame from the stream channel (snapshots, logs, profiler samples). Frames may be dropped.</summary>
public sealed record StreamMessage(string Stream, ulong Sequence, ReadOnlyMemory<byte> Payload);

/// <summary>The engine refused the handshake, for example because of a protocol version mismatch.</summary>
public sealed class RpcRejectedException : Exception
{
    public RpcRejectedException(string errorCode, string message)
        : base(string.IsNullOrEmpty(message) ? $"The engine rejected the connection ({errorCode})." : message)
    {
        ErrorCode = errorCode;
    }

    /// <summary><c>Unsupported</c> for a version mismatch, <c>InvalidArgument</c> for a refused pairing token.</summary>
    public string ErrorCode { get; }
}
