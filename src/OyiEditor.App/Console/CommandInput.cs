using System.Text;

namespace OyiEditor.App.Console;

/// <summary>One parsed console line: <c>command arg1 arg2 -- payload text</c>.</summary>
internal sealed record CommandInput(string Command, IReadOnlyList<string> Args, byte[] Payload)
{
    private const string PayloadSeparator = "--";

    /// <summary>
    /// Splits on whitespace (no quoting). Everything after a standalone <c>--</c> is sent verbatim as the UTF-8
    /// payload, so <c>echo -- hello world</c> echoes "hello world".
    /// </summary>
    public static bool TryParse(string line, out CommandInput input)
    {
        var head = line;
        var payload = Array.Empty<byte>();
        var separator = FindSeparator(line);
        if (separator >= 0)
        {
            head = line[..separator];
            var rest = line[(separator + PayloadSeparator.Length)..];
            payload = Encoding.UTF8.GetBytes(rest.StartsWith(' ') ? rest[1..] : rest);
        }

        var words = head.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            input = new CommandInput(string.Empty, [], []);
            return false;
        }

        input = new CommandInput(words[0], words[1..], payload);
        return true;
    }

    private static int FindSeparator(string line)
    {
        for (var index = line.IndexOf(PayloadSeparator, StringComparison.Ordinal);
             index >= 0;
             index = line.IndexOf(PayloadSeparator, index + 1, StringComparison.Ordinal))
        {
            var end = index + PayloadSeparator.Length;
            var standalone = (index == 0 || char.IsWhiteSpace(line[index - 1]))
                             && (end == line.Length || char.IsWhiteSpace(line[end]));
            if (standalone)
            {
                return index;
            }
        }

        return -1;
    }
}
