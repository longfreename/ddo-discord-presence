using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DdoDiscordPresence.Discord
{
    /// <summary>Discord RPC opcodes. Every frame on the pipe is one of these.</summary>
    internal enum IpcOpcode
    {
        Handshake = 0,
        Frame = 1,
        Close = 2,
        Ping = 3,
        Pong = 4,
    }

    /// <summary>One decoded frame: the opcode and its UTF-8 JSON payload.</summary>
    internal readonly struct IpcFrame
    {
        public IpcFrame(IpcOpcode opcode, string json) { Opcode = opcode; Json = json; }
        public IpcOpcode Opcode { get; }
        public string Json { get; }
        public override string ToString() => $"{Opcode}: {Json}";
    }

    /// <summary>The pipe broke the framing rules (bad opcode, negative or oversized length, invalid UTF-8).</summary>
    internal sealed class IpcProtocolException : IOException
    {
        public IpcProtocolException(string message) : base(message) { }
    }

    /// <summary>
    /// Frame encoder and decoder for the Discord local RPC pipe. A frame is a little-endian int32 opcode, a little-endian
    /// int32 payload length, then that many bytes of UTF-8 JSON. Reads tolerate partial reads (a pipe may hand back any
    /// number of bytes per read) and refuse oversized payloads instead of allocating whatever the length field says.
    /// </summary>
    internal static class IpcCodec
    {
        public const int HeaderSize = 8;
        /// <summary>Discord's own frames are a few kilobytes; anything beyond this is a broken or hostile peer.</summary>
        public const int MaxPayload = 64 * 1024;

        private static readonly UTF8Encoding Utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        public static byte[] Encode(IpcOpcode opcode, string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            var payload = Utf8Strict.GetBytes(json);
            if (payload.Length > MaxPayload) throw new ArgumentException($"payload of {payload.Length} bytes exceeds {MaxPayload}", nameof(json));
            var frame = new byte[HeaderSize + payload.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), (int)opcode);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), payload.Length);
            payload.CopyTo(frame, HeaderSize);
            return frame;
        }

        /// <summary>Decodes a complete frame from a buffer (header + payload). Used by tests and by <see cref="ReadAsync"/>.</summary>
        public static bool TryParseHeader(ReadOnlySpan<byte> header, out IpcOpcode opcode, out int length, int maxPayload = MaxPayload)
        {
            opcode = default; length = 0;
            if (header.Length < HeaderSize) return false;
            var op = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(0, 4));
            var len = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(4, 4));
            if (op < (int)IpcOpcode.Handshake || op > (int)IpcOpcode.Pong) throw new IpcProtocolException($"unknown opcode {op}");
            if (len < 0 || len > maxPayload) throw new IpcProtocolException($"frame length {len} outside 0..{maxPayload}");
            opcode = (IpcOpcode)op; length = len;
            return true;
        }

        /// <summary>Reads exactly one frame. Throws <see cref="EndOfStreamException"/> when the pipe closes mid-way.</summary>
        public static async Task<IpcFrame> ReadAsync(Stream stream, CancellationToken ct, int maxPayload = MaxPayload)
        {
            var header = new byte[HeaderSize];
            await ReadExactlyAsync(stream, header, ct).ConfigureAwait(false);
            TryParseHeader(header, out var opcode, out var length, maxPayload);
            var payload = new byte[length];
            if (length > 0) await ReadExactlyAsync(stream, payload, ct).ConfigureAwait(false);
            string json;
            try { json = Utf8Strict.GetString(payload); }
            catch (DecoderFallbackException) { throw new IpcProtocolException("payload is not valid UTF-8"); }
            return new IpcFrame(opcode, json);
        }

        private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct).ConfigureAwait(false);
                if (n <= 0) throw new EndOfStreamException("the Discord pipe closed");
                read += n;
            }
        }
    }
}
