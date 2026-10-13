using System.Text;
using OyiEditor.Protocol;

namespace OyiEditor.App.Console;

/// <summary>Turns replies and stream frames into console lines.</summary>
internal static class ReplyFormat
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static IEnumerable<string> Describe(RpcReply reply)
    {
        if (reply.Ok)
        {
            yield return string.IsNullOrEmpty(reply.Message) ? "ok" : $"ok: {reply.Message}";
        }
        else
        {
            yield return string.IsNullOrEmpty(reply.Message)
                ? $"error {reply.ErrorCode}"
                : $"error {reply.ErrorCode}: {reply.Message}";
        }

        if (!reply.Payload.IsEmpty)
        {
            yield return Payload(reply.Payload.Span);
        }
    }

    public static string Describe(StreamMessage frame) =>
        frame.Payload.IsEmpty
            ? $"[{frame.Stream} #{frame.Sequence}]"
            : $"[{frame.Stream} #{frame.Sequence}] {Payload(frame.Payload.Span)}";

    /// <summary>Printable UTF-8 is shown as text; anything else as its size.</summary>
    public static string Payload(ReadOnlySpan<byte> bytes)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return $"<{bytes.Length} bytes>";
        }

        foreach (var c in text)
        {
            if (char.IsControl(c) && c is not ('\n' or '\r' or '\t'))
            {
                return $"<{bytes.Length} bytes>";
            }
        }

        return text;
    }
}
