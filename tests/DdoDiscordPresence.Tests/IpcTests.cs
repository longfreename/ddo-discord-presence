using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DdoDiscordPresence.Discord;

namespace DdoDiscordPresence.Tests
{
    /// <summary>A stream that hands back at most N bytes per read, as a pipe may.</summary>
    internal sealed class TrickleStream : Stream
    {
        private readonly MemoryStream _inner;
        private readonly int _chunk;
        public TrickleStream(byte[] data, int chunk) { _inner = new MemoryStream(data); _chunk = chunk; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, _chunk));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            new ValueTask<int>(_inner.Read(buffer.Span.Slice(0, Math.Min(buffer.Length, _chunk))));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    internal static class CodecTests
    {
        [Test]
        public static void EncodeWritesLittleEndianHeaderAndUtf8()
        {
            var bytes = IpcCodec.Encode(IpcOpcode.Frame, "{\"a\":\"é\"}");
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, 4)), "opcode");
            var payload = Encoding.UTF8.GetBytes("{\"a\":\"é\"}");
            Assert.Equal(payload.Length, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4)), "length counts UTF-8 bytes");
            Assert.True(bytes.Skip(8).SequenceEqual(payload), "payload bytes");
            Assert.Equal(0x01, (int)bytes[0], "low byte first");
        }

        [Test]
        public static async Task RoundTripEveryOpcode()
        {
            foreach (IpcOpcode op in Enum.GetValues(typeof(IpcOpcode)))
            {
                var f = await IpcCodec.ReadAsync(new MemoryStream(IpcCodec.Encode(op, "{\"x\":1}")), CancellationToken.None);
                Assert.Equal(op, f.Opcode, "opcode " + op);
                Assert.Equal("{\"x\":1}", f.Json, "json " + op);
            }
        }

        [Test]
        public static async Task PartialReadsOneByteAtATime()
        {
            var a = IpcCodec.Encode(IpcOpcode.Frame, "{\"first\":true}");
            var b = IpcCodec.Encode(IpcOpcode.Ping, "{\"second\":\"☃\"}");
            var s = new TrickleStream(a.Concat(b).ToArray(), 1);
            var f1 = await IpcCodec.ReadAsync(s, CancellationToken.None);
            var f2 = await IpcCodec.ReadAsync(s, CancellationToken.None);
            Assert.Equal("{\"first\":true}", f1.Json, "first frame");
            Assert.Equal(IpcOpcode.Ping, f2.Opcode, "second opcode");
            Assert.Equal("{\"second\":\"☃\"}", f2.Json, "second frame (multi-byte char split across reads)");
        }

        [Test]
        public static async Task PartialReadsOddChunks()
        {
            var json = "{\"pad\":\"" + new string('x', 5000) + "\"}";
            var s = new TrickleStream(IpcCodec.Encode(IpcOpcode.Frame, json), 7);
            var f = await IpcCodec.ReadAsync(s, CancellationToken.None);
            Assert.Equal(json, f.Json, "5 KB payload in 7-byte reads");
        }

        [Test]
        public static async Task EmptyPayloadIsAllowed()
        {
            var f = await IpcCodec.ReadAsync(new MemoryStream(IpcCodec.Encode(IpcOpcode.Pong, "")), CancellationToken.None);
            Assert.Equal("", f.Json, "empty payload");
        }

        private static byte[] Header(int op, int len)
        {
            var h = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(0, 4), op);
            BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(4, 4), len);
            return h;
        }

        [Test]
        public static async Task OversizedFrameIsRefusedWithoutAllocating()
        {
            await Assert.ThrowsAsync<IpcProtocolException>(() => IpcCodec.ReadAsync(new MemoryStream(Header(1, IpcCodec.MaxPayload + 1)), CancellationToken.None), "oversized");
            await Assert.ThrowsAsync<IpcProtocolException>(() => IpcCodec.ReadAsync(new MemoryStream(Header(1, int.MaxValue)), CancellationToken.None), "int.MaxValue");
            await Assert.ThrowsAsync<IpcProtocolException>(() => IpcCodec.ReadAsync(new MemoryStream(Header(1, -5)), CancellationToken.None), "negative");
            await Assert.ThrowsAsync<IpcProtocolException>(() => IpcCodec.ReadAsync(new MemoryStream(Header(9, 2)), CancellationToken.None), "unknown opcode");
        }

        [Test]
        public static void EncodeRefusesOversizedPayload()
        {
            Assert.Throws<ArgumentException>(() => IpcCodec.Encode(IpcOpcode.Frame, new string('a', IpcCodec.MaxPayload + 1)), "encode oversized");
        }

        [Test]
        public static async Task TruncatedFrameIsEndOfStream()
        {
            var full = IpcCodec.Encode(IpcOpcode.Frame, "{\"abc\":123}");
            await Assert.ThrowsAsync<EndOfStreamException>(() => IpcCodec.ReadAsync(new MemoryStream(full.Take(full.Length - 3).ToArray()), CancellationToken.None), "cut payload");
            await Assert.ThrowsAsync<EndOfStreamException>(() => IpcCodec.ReadAsync(new MemoryStream(full.Take(5).ToArray()), CancellationToken.None), "cut header");
        }

        [Test]
        public static async Task InvalidUtf8IsAProtocolError()
        {
            var bad = Header(1, 2).Concat(new byte[] { 0xC3, 0x28 }).ToArray();
            await Assert.ThrowsAsync<IpcProtocolException>(() => IpcCodec.ReadAsync(new MemoryStream(bad), CancellationToken.None), "invalid UTF-8");
        }
    }

    internal static class ConnectionTests
    {
        private const string Id = "123456789012345678";

        [Test]
        public static async Task HandshakeWaitsForReadyAndReadsTheUser()
        {
            await using var fake = new FakeDiscord { UserName = "someone" }.Start();
            await using var conn = await DiscordRpcConnection.OpenAsync(fake.Connector(), Id, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Equal("someone", conn.User.Username, "READY user");
            Assert.True(fake.Received.TryPeek(out var hello), "handshake received");
            Assert.Equal(IpcOpcode.Handshake, hello.Opcode, "first frame is HANDSHAKE");
            var node = JsonNode.Parse(hello.Json)!;
            Assert.Equal(1, node["v"]!.GetValue<int>(), "v");
            Assert.Equal(Id, node["client_id"]!.GetValue<string>(), "client_id");
            Assert.True(conn.ReadyJson.Contains("\"READY\""), "READY payload kept");
        }

        [Test]
        public static async Task HandshakeCloseIsReportedWithDiscordsCode()
        {
            await using var fake = new FakeDiscord { Mode = FakeDiscord.HandshakeMode.CloseInvalidClientId }.Start();
            var ex = await Assert.ThrowsAsync<DiscordRpcException>(() => DiscordRpcConnection.OpenAsync(fake.Connector(), "000000000000000000", TimeSpan.FromSeconds(5), CancellationToken.None), "invalid id");
            Assert.Equal(4000, ex.Code, "code");
            Assert.Equal("Invalid Client ID", ex.DiscordMessage, "message");
        }

        [Test]
        public static async Task SilentDiscordTimesOut()
        {
            await using var fake = new FakeDiscord { Mode = FakeDiscord.HandshakeMode.Silent }.Start();
            var sw = Stopwatch.StartNew();
            await Assert.ThrowsAsync<TimeoutException>(() => DiscordRpcConnection.OpenAsync(fake.Connector(), Id, TimeSpan.FromMilliseconds(400), CancellationToken.None), "silent");
            Assert.True(sw.ElapsedMilliseconds < 3000, "timeout honoured");
        }

        [Test]
        public static async Task NoPipeMeansDiscordNotRunningQuickly()
        {
            var connector = new NamedPipeConnector("ddp-test-absent-" + Guid.NewGuid().ToString("N") + "-", 10, TimeSpan.FromSeconds(1));
            var sw = Stopwatch.StartNew();
            await Assert.ThrowsAsync<DiscordNotRunningException>(() => connector.ConnectAsync(CancellationToken.None), "no pipe");
            Assert.True(sw.ElapsedMilliseconds < 1500, $"absent pipes are skipped, not waited for ({sw.ElapsedMilliseconds} ms)");
        }

        [Test]
        public static async Task SetActivitySendsPidActivityAndNonceAndReadsTheReply()
        {
            await using var fake = new FakeDiscord().Start();
            await using var conn = await DiscordRpcConnection.OpenAsync(fake.Connector(), Id, TimeSpan.FromSeconds(5), CancellationToken.None);
            var reply = await conn.SetActivityAsync(4242, new JsonObject { ["details"] = "The Harbor" }, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.True(reply.Ok, "accepted");
            var args = fake.ActivityList().Single();
            Assert.Equal(4242, args["pid"]!.GetValue<int>(), "pid");
            Assert.Equal("The Harbor", args["activity"]!["details"]!.GetValue<string>(), "activity");
            var frame = fake.Received.Last(f => f.Opcode == IpcOpcode.Frame);
            var node = JsonNode.Parse(frame.Json)!;
            Assert.Equal("SET_ACTIVITY", node["cmd"]!.GetValue<string>(), "cmd");
            Assert.True(Guid.TryParse(node["nonce"]!.GetValue<string>(), out _), "nonce is a GUID");
        }

        [Test]
        public static async Task ClearingSendsActivityNull()
        {
            await using var fake = new FakeDiscord().Start();
            await using var conn = await DiscordRpcConnection.OpenAsync(fake.Connector(), Id, TimeSpan.FromSeconds(5), CancellationToken.None);
            var reply = await conn.SetActivityAsync(1, null, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.True(reply.Ok, "accepted");
            var frame = fake.Received.Last(f => f.Opcode == IpcOpcode.Frame);
            Assert.True(frame.Json.Contains("\"activity\":null"), "activity:null on the wire: " + frame.Json);
        }

        [Test]
        public static async Task ErrorReplyCarriesCodeAndMessage()
        {
            await using var fake = new FakeDiscord { ActivityError = (4002, "child \"activity\" fails because [child \"details\" fails]") }.Start();
            await using var conn = await DiscordRpcConnection.OpenAsync(fake.Connector(), Id, TimeSpan.FromSeconds(5), CancellationToken.None);
            var reply = await conn.SetActivityAsync(1, new JsonObject { ["details"] = "x" }, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.True(!reply.Ok, "error reply");
            Assert.Equal(4002, reply.Code, "code");
            Assert.True(reply.Message!.Contains("details"), "message");
        }

        [Test]
        public static async Task PingIsAnsweredWithPongCarryingTheSamePayload()
        {
            await using var fake = new FakeDiscord().Start();
            await using var conn = await DiscordRpcConnection.OpenAsync(fake.Connector(), Id, TimeSpan.FromSeconds(5), CancellationToken.None);
            await fake.SendPingAsync("{\"ping\":17}");
            var pong = await fake.PongReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("{\"ping\":17}", pong, "PONG payload");
            Assert.True(!conn.Closed.IsCompleted, "still open after PING");
        }

        [Test]
        public static async Task CloseFrameEndsTheConnectionWithItsCode()
        {
            await using var fake = new FakeDiscord().Start();
            await using var conn = await DiscordRpcConnection.OpenAsync(fake.Connector(), Id, TimeSpan.FromSeconds(5), CancellationToken.None);
            await fake.SendCloseAsync(4005, "going away");
            var info = await conn.Closed.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(4005, info.Code, "close code");
            Assert.Equal("going away", info.Message, "close message");
            await Assert.ThrowsAsync<IOException>(() => conn.SetActivityAsync(1, null, TimeSpan.FromSeconds(1), CancellationToken.None), "commands after close");
        }

        [Test]
        public static async Task PipeBreakEndsTheConnectionAndFailsPendingCommands()
        {
            await using var fake = new FakeDiscord { AnswerCommands = false }.Start();
            await using var conn = await DiscordRpcConnection.OpenAsync(fake.Connector(), Id, TimeSpan.FromSeconds(5), CancellationToken.None);
            var pending = conn.SetActivityAsync(1, new JsonObject { ["details"] = "abc" }, TimeSpan.FromSeconds(10), CancellationToken.None);
            await Assert.Eventually(() => fake.SetActivityCount == 1, "command reached the fake");
            fake.DropClient();
            var info = await conn.Closed.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(!info.ByDiscord && info.Error != null, "closed by I/O error");
            await Assert.ThrowsAsync<IOException>(() => pending, "pending command fails, does not hang");
        }

        [Test]
        public static async Task UnansweredCommandTimesOut()
        {
            await using var fake = new FakeDiscord { AnswerCommands = false }.Start();
            await using var conn = await DiscordRpcConnection.OpenAsync(fake.Connector(), Id, TimeSpan.FromSeconds(5), CancellationToken.None);
            await Assert.ThrowsAsync<TimeoutException>(() => conn.SetActivityAsync(1, null, TimeSpan.FromMilliseconds(300), CancellationToken.None), "no reply");
        }
    }
}
