using System.Buffers;
using System.Buffers.Binary;

namespace OyiEditor.Protocol;

/// <summary>
/// Wire framing shared with the engine's <c>oyi_rpc</c>: every frame is a 32-bit little-endian length followed by
/// that many bytes (one <c>Envelope</c> FlatBuffer). Lengths above <see cref="MaxFrameSize"/> are a protocol error.
/// </summary>
public static class Framing
{
    public const int HeaderSize = sizeof(uint);
    public const int MaxFrameSize = 16 * 1024 * 1024;

    /// <summary>Writes the length header and the payload.</summary>
    /// <exception cref="ArgumentException">The payload is larger than <see cref="MaxFrameSize"/>.</exception>
    public static void AppendFrame(IBufferWriter<byte> output, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (payload.Length > MaxFrameSize)
        {
            throw new ArgumentException(
                $"Frame payload of {payload.Length} bytes exceeds the protocol limit of {MaxFrameSize} bytes.",
                nameof(payload));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(output.GetSpan(HeaderSize), (uint)payload.Length);
        output.Advance(HeaderSize);
        output.Write(payload);
    }
}

/// <summary>
/// Reassembles frames from arbitrarily fragmented reads. Push whatever the socket returned, then drain with
/// <see cref="HasFrame"/> / <see cref="PeekFrame"/> / <see cref="PopFrame"/>.
/// </summary>
public sealed class FrameReader
{
    private const int MinimumCapacity = 256;

    private byte[] _buffer = [];
    private int _start;
    private int _count;

    /// <summary>True when a complete frame is at the front of the buffer.</summary>
    public bool HasFrame => _count >= Framing.HeaderSize && _count - Framing.HeaderSize >= FrontLength;

    /// <summary>Number of buffered bytes that have not been popped yet, headers included.</summary>
    public int BufferedBytes => _count;

    private int FrontLength => (int)BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(_start, Framing.HeaderSize));

    /// <summary>Appends received bytes.</summary>
    /// <exception cref="ProtocolException">
    /// A buffered length header exceeds <see cref="Framing.MaxFrameSize"/>. The reader is cleared.
    /// </exception>
    public void Push(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        EnsureSpace(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_start + _count));
        _count += bytes.Length;
        ValidateHeaders();
    }

    /// <summary>The payload of the front frame, or an empty span when <see cref="HasFrame"/> is false.</summary>
    /// <remarks>The span is invalidated by the next <see cref="Push"/>, <see cref="PopFrame"/> or <see cref="Clear"/>.</remarks>
    public ReadOnlySpan<byte> PeekFrame() =>
        HasFrame ? _buffer.AsSpan(_start + Framing.HeaderSize, FrontLength) : ReadOnlySpan<byte>.Empty;

    /// <summary>Drops the front frame; does nothing when <see cref="HasFrame"/> is false.</summary>
    public void PopFrame()
    {
        if (!HasFrame)
        {
            return;
        }

        var consumed = Framing.HeaderSize + FrontLength;
        _start += consumed;
        _count -= consumed;
        if (_count == 0)
        {
            _start = 0;
        }
    }

    public void Clear()
    {
        _start = 0;
        _count = 0;
    }

    // Checks every complete header in the buffer, not only the front one, so an oversize frame queued behind
    // complete frames is rejected as soon as its header arrives instead of stalling the reader.
    private void ValidateHeaders()
    {
        long offset = _start;
        long end = _start + _count;
        while (end - offset >= Framing.HeaderSize)
        {
            var length = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan((int)offset, Framing.HeaderSize));
            if (length > Framing.MaxFrameSize)
            {
                Clear();
                throw new ProtocolException(
                    $"Frame length {length} exceeds the protocol limit of {Framing.MaxFrameSize} bytes.");
            }

            offset += Framing.HeaderSize + length;
        }
    }

    private void EnsureSpace(int extra)
    {
        if ((long)_start + _count + extra <= _buffer.Length)
        {
            return;
        }

        var required = (long)_count + extra;
        if (required <= _buffer.Length)
        {
            _buffer.AsSpan(_start, _count).CopyTo(_buffer);
            _start = 0;
            return;
        }

        var capacity = Math.Max(Math.Max(MinimumCapacity, (long)_buffer.Length * 2), required);
        var grown = new byte[Math.Min(capacity, Array.MaxLength)];
        _buffer.AsSpan(_start, _count).CopyTo(grown);
        _buffer = grown;
        _start = 0;
    }
}
