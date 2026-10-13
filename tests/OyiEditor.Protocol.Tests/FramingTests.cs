using System.Buffers;
using System.Text;

namespace OyiEditor.Protocol.Tests;

// Mirrors the engine's "frames reassemble from any fragmentation and oversize lengths are refused" case
// (OyiEngine tests/oyi_rpc_tests/RpcTests.cpp) and adds C#-side edge cases.
public sealed class FramingTests
{
    [Fact]
    public void AppendFrameWritesLittleEndianLengthThenPayload()
    {
        var wire = new ArrayBufferWriter<byte>();
        Framing.AppendFrame(wire, "hello"u8);

        byte[] expected = [5, 0, 0, 0, .. "hello"u8.ToArray()];
        Assert.Equal(expected, wire.WrittenSpan.ToArray());
    }

    [Fact]
    public void AppendFrameRejectsPayloadAboveLimit()
    {
        var wire = new ArrayBufferWriter<byte>();
        var payload = new byte[Framing.MaxFrameSize + 1];

        Assert.Throws<ArgumentException>(() => Framing.AppendFrame(wire, payload));
        Assert.Equal(0, wire.WrittenCount);
    }

    [Fact]
    public void FramesReassembleFromSingleByteFragments()
    {
        var wire = new ArrayBufferWriter<byte>();
        Framing.AppendFrame(wire, "hello"u8);
        Framing.AppendFrame(wire, ""u8);
        Framing.AppendFrame(wire, "world!"u8);
        Assert.Equal(4 + 5 + 4 + 0 + 4 + 6, wire.WrittenCount);

        var reader = new FrameReader();
        foreach (var b in wire.WrittenSpan)
        {
            reader.Push([b]);
        }

        Assert.True(reader.HasFrame);
        Assert.Equal("hello", Text(reader.PeekFrame()));
        reader.PopFrame();
        Assert.True(reader.HasFrame);
        Assert.True(reader.PeekFrame().IsEmpty);
        reader.PopFrame();
        Assert.True(reader.HasFrame);
        Assert.Equal("world!", Text(reader.PeekFrame()));
        reader.PopFrame();
        Assert.False(reader.HasFrame);
        Assert.True(reader.PeekFrame().IsEmpty);
        reader.PopFrame(); // harmless
        Assert.Equal(0, reader.BufferedBytes);
    }

    [Fact]
    public void OversizeLengthIsRefusedAndClearsTheReader()
    {
        var reader = new FrameReader();
        reader.Push([0x01, 0x00]);

        Assert.Throws<ProtocolException>(() => reader.Push([0x00, 0x01]));
        Assert.False(reader.HasFrame);
        Assert.Equal(0, reader.BufferedBytes);
        Assert.Throws<ProtocolException>(() => reader.Push([0xFF, 0xFF, 0xFF, 0x7F]));
    }

    [Fact]
    public void OversizeLengthBehindACompleteFrameIsRefusedImmediately()
    {
        var wire = new ArrayBufferWriter<byte>();
        Framing.AppendFrame(wire, "ok"u8);
        wire.Write(new byte[] { 0x01, 0x00, 0x00, 0x01 });

        var reader = new FrameReader();
        Assert.Throws<ProtocolException>(() => reader.Push(wire.WrittenSpan));
    }

    [Fact]
    public void LengthAtTheLimitIsAccepted()
    {
        Assert.Equal(16 * 1024 * 1024, Framing.MaxFrameSize);

        var reader = new FrameReader();
        reader.Push([0x00, 0x00, 0x00, 0x01]); // exactly 16 MiB

        Assert.False(reader.HasFrame);
        Assert.Equal(4, reader.BufferedBytes);
    }

    [Fact]
    public void RandomChunkingAcrossGrowthAndCompactionPreservesFrames()
    {
        var random = new Random(20261013);
        var payloads = Enumerable.Range(0, 200)
            .Select(i => RandomBytes(random, random.Next(0, i % 10 == 0 ? 5000 : 64)))
            .ToList();
        var wire = new ArrayBufferWriter<byte>();
        foreach (var payload in payloads)
        {
            Framing.AppendFrame(wire, payload);
        }

        var reader = new FrameReader();
        var received = new List<byte[]>();
        var bytes = wire.WrittenSpan.ToArray();
        for (var offset = 0; offset < bytes.Length;)
        {
            var chunk = Math.Min(random.Next(1, 700), bytes.Length - offset);
            reader.Push(bytes.AsSpan(offset, chunk));
            offset += chunk;
            while (reader.HasFrame)
            {
                received.Add(reader.PeekFrame().ToArray());
                reader.PopFrame();
            }
        }

        Assert.Equal(payloads, received);
        Assert.Equal(0, reader.BufferedBytes);
    }

    private static string Text(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);

    private static byte[] RandomBytes(Random random, int length)
    {
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }
}
