namespace OyiEditor.Protocol;

/// <summary>
/// The peer sent bytes that violate the wire protocol. The connection cannot be trusted afterwards and must be closed.
/// </summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException()
    {
    }

    public ProtocolException(string message)
        : base(message)
    {
    }

    public ProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
