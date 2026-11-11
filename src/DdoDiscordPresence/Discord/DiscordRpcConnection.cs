using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace DdoDiscordPresence.Discord
{
    /// <summary>Opens the local pipe to Discord. Injectable so tests can point it at their own pipe server.</summary>
    internal interface IPipeConnector
    {
        Task<Stream> ConnectAsync(CancellationToken ct);
    }

    /// <summary>No Discord pipe exists (Discord is not running, or runs as another Windows user).</summary>
    internal sealed class DiscordNotRunningException : Exception
    {
        public DiscordNotRunningException(string message) : base(message) { }
    }

    /// <summary>Discord refused something and said why (a CLOSE frame or an ERROR reply): its numeric code and message.</summary>
    internal sealed class DiscordRpcException : Exception
    {
        public DiscordRpcException(int? code, string? discordMessage)
            : base(Describe(code, discordMessage)) { Code = code; DiscordMessage = discordMessage; }
        public int? Code { get; }
        public string? DiscordMessage { get; }
        internal static string Describe(int? code, string? message) =>
            code.HasValue ? $"{code} {message ?? ""}".Trim() : (string.IsNullOrEmpty(message) ? "no reason given" : message!);
    }

    /// <summary>
    /// Connects to \\.\pipe\{prefix}0 .. {prefix}{count-1}, first that answers. Only pipes that exist are tried (the pipe
    /// namespace is listed first), so a missing Discord costs a directory listing, not ten blocking connect timeouts.
    /// </summary>
    internal sealed class NamedPipeConnector : IPipeConnector
    {
        public const string DiscordPrefix = "discord-ipc-";
        private readonly string _prefix;
        private readonly int _count;
        private readonly TimeSpan _perPipeTimeout;

        public NamedPipeConnector(string prefix = DiscordPrefix, int count = 10, TimeSpan? perPipeTimeout = null)
        {
            _prefix = prefix;
            _count = Math.Max(1, count);
            _perPipeTimeout = perPipeTimeout ?? TimeSpan.FromSeconds(1);
        }

        public string Prefix => _prefix;

        public async Task<Stream> ConnectAsync(CancellationToken ct)
        {
            var existing = ExistingPipes();
            Exception? last = null;
            for (var i = 0; i < _count; i++)
            {
                var name = _prefix + i;
                if (existing != null && !existing.Contains(name)) continue;
                var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync((int)_perPipeTimeout.TotalMilliseconds, ct).ConfigureAwait(false);
                    return pipe;
                }
                catch (OperationCanceledException) { pipe.Dispose(); throw; }
                catch (Exception ex) when (ex is TimeoutException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    pipe.Dispose();
                    last = ex;
                }
            }
            throw new DiscordNotRunningException(last == null ? "Discord is not running (no Discord pipe found)" : "Discord's pipe did not accept a connection: " + last.Message);
        }

        /// <summary>The pipe names that exist right now, or null when the namespace cannot be listed (then every index is tried).</summary>
        private static HashSet<string>? ExistingPipes()
        {
            try
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var full in Directory.EnumerateFiles(@"\\.\pipe\"))
                {
                    var name = full.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase) ? full.Substring(9) : Path.GetFileName(full);
                    set.Add(name);
                }
                return set;
            }
            catch { return null; }
        }
    }

    /// <summary>The user Discord reported in READY. Only shown in the plugin's own status line; never logged in full.</summary>
    internal sealed class DiscordUser
    {
        public string Id { get; init; } = "";
        public string Username { get; init; } = "";
        public string? GlobalName { get; init; }
        public string Display => !string.IsNullOrWhiteSpace(GlobalName) ? GlobalName! : Username;
    }

    /// <summary>Discord's answer to one command.</summary>
    internal sealed class RpcReply
    {
        public bool Ok { get; init; }
        public int? Code { get; init; }
        public string? Message { get; init; }
        public string Raw { get; init; } = "";
        public string Describe() => Ok ? "accepted" : "error " + DiscordRpcException.Describe(Code, Message);
    }

    /// <summary>Why a connection ended: Discord's CLOSE code/message, or the I/O error.</summary>
    internal sealed class CloseInfo
    {
        public int? Code { get; init; }
        public string? Message { get; init; }
        public Exception? Error { get; init; }
        public bool ByDiscord => Code.HasValue;
        public string Describe() => Code.HasValue ? DiscordRpcException.Describe(Code, Message)
            : Error != null ? Error.Message : (Message ?? "closed");
    }

    /// <summary>
    /// One live RPC connection: HANDSHAKE, wait for READY, then commands. A background reader answers PING with PONG,
    /// matches replies to commands by nonce, and completes <see cref="Closed"/> on CLOSE or any pipe error. No method here
    /// throws into a caller after the connection is open except for cancellation and timeouts it was asked to observe.
    /// </summary>
    internal sealed class DiscordRpcConnection : IAsyncDisposable
    {
        private readonly Stream _stream;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<RpcReply>> _pending = new ConcurrentDictionary<string, TaskCompletionSource<RpcReply>>();
        private readonly TaskCompletionSource<CloseInfo> _closed = new TaskCompletionSource<CloseInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _reader;
        private int _disposed;

        private DiscordRpcConnection(Stream stream) { _stream = stream; }

        public DiscordUser User { get; private set; } = new DiscordUser();
        /// <summary>The READY payload exactly as Discord sent it (for the probe tool).</summary>
        public string ReadyJson { get; private set; } = "";
        public Task<CloseInfo> Closed => _closed.Task;

        public static async Task<DiscordRpcConnection> OpenAsync(IPipeConnector connector, string clientId, TimeSpan timeout, CancellationToken ct)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            Stream stream;
            try { stream = await connector.ConnectAsync(timeoutCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("timed out connecting to Discord's pipe"); }
            var conn = new DiscordRpcConnection(stream);
            try
            {
                await conn.HandshakeAsync(clientId, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await conn.DisposeAsync().ConfigureAwait(false);
                throw new TimeoutException("Discord did not answer the handshake in time");
            }
            catch
            {
                await conn.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            conn._reader = Task.Run(() => conn.ReadLoopAsync());
            return conn;
        }

        private async Task HandshakeAsync(string clientId, CancellationToken ct)
        {
            var hello = new JsonObject { ["v"] = 1, ["client_id"] = clientId };
            await WriteAsync(IpcOpcode.Handshake, hello.ToJsonString(), ct).ConfigureAwait(false);
            while (true)
            {
                var frame = await IpcCodec.ReadAsync(_stream, ct).ConfigureAwait(false);
                switch (frame.Opcode)
                {
                    case IpcOpcode.Close:
                        var (code, message) = ReadCodeMessage(frame.Json, dataField: false);
                        throw new DiscordRpcException(code, message);
                    case IpcOpcode.Ping:
                        await WriteAsync(IpcOpcode.Pong, frame.Json, ct).ConfigureAwait(false);
                        continue;
                    case IpcOpcode.Frame:
                        JsonNode? node;
                        try { node = JsonNode.Parse(frame.Json); }
                        catch (JsonException) { throw new IpcProtocolException("Discord sent a frame that is not JSON"); }
                        var cmd = node?["cmd"]?.GetValue<string>();
                        var evt = node?["evt"]?.GetValue<string>();
                        if (cmd == "DISPATCH" && evt == "READY")
                        {
                            ReadyJson = frame.Json;
                            var user = node?["data"]?["user"];
                            User = new DiscordUser
                            {
                                Id = Str(user?["id"]) ?? "",
                                Username = Str(user?["username"]) ?? "",
                                GlobalName = Str(user?["global_name"]),
                            };
                            return;
                        }
                        if (evt == "ERROR")
                        {
                            var (c, m) = ReadCodeMessage(frame.Json, dataField: true);
                            throw new DiscordRpcException(c, m);
                        }
                        continue;
                    default:
                        continue;
                }
            }
        }

        private async Task ReadLoopAsync()
        {
            CloseInfo info;
            try
            {
                while (true)
                {
                    var frame = await IpcCodec.ReadAsync(_stream, _cts.Token).ConfigureAwait(false);
                    if (frame.Opcode == IpcOpcode.Ping)
                    {
                        await WriteAsync(IpcOpcode.Pong, frame.Json, _cts.Token).ConfigureAwait(false);
                        continue;
                    }
                    if (frame.Opcode == IpcOpcode.Close)
                    {
                        var (code, message) = ReadCodeMessage(frame.Json, dataField: false);
                        info = new CloseInfo { Code = code, Message = message };
                        break;
                    }
                    if (frame.Opcode != IpcOpcode.Frame) continue;
                    JsonNode? node;
                    try { node = JsonNode.Parse(frame.Json); }
                    catch (JsonException) { continue; }
                    var nonce = Str(node?["nonce"]);
                    if (nonce == null || !_pending.TryRemove(nonce, out var tcs)) continue;
                    var isError = Str(node?["evt"]) == "ERROR";
                    var (c, m) = isError ? ReadCodeMessage(frame.Json, dataField: true) : (null, null);
                    tcs.TrySetResult(new RpcReply { Ok = !isError, Code = c, Message = m, Raw = frame.Json });
                }
            }
            catch (OperationCanceledException) { info = new CloseInfo { Message = "closed by the plugin" }; }
            catch (Exception ex) { info = new CloseInfo { Error = ex }; }
            FailPending(info);
            _closed.TrySetResult(info);
        }

        private void FailPending(CloseInfo info)
        {
            foreach (var kv in _pending)
                if (_pending.TryRemove(kv.Key, out var tcs))
                    tcs.TrySetException(new IOException("Discord connection closed: " + info.Describe()));
        }

        /// <summary>SET_ACTIVITY for <paramref name="pid"/>; null activity clears it. Throws on timeout or a closed pipe.</summary>
        public Task<RpcReply> SetActivityAsync(int pid, JsonObject? activity, TimeSpan timeout, CancellationToken ct)
        {
            var args = new JsonObject { ["pid"] = pid, ["activity"] = activity };
            return CommandAsync("SET_ACTIVITY", args, timeout, ct);
        }

        public async Task<RpcReply> CommandAsync(string cmd, JsonObject args, TimeSpan timeout, CancellationToken ct)
        {
            if (_closed.Task.IsCompleted) throw new IOException("Discord connection is closed: " + _closed.Task.Result.Describe());
            var nonce = Guid.NewGuid().ToString();
            var tcs = new TaskCompletionSource<RpcReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[nonce] = tcs;
            try
            {
                var payload = new JsonObject { ["cmd"] = cmd, ["args"] = args, ["nonce"] = nonce };
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
                timeoutCts.CancelAfter(timeout);
                await WriteAsync(IpcOpcode.Frame, payload.ToJsonString(), timeoutCts.Token).ConfigureAwait(false);
                var done = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token)).ConfigureAwait(false);
                if (done == tcs.Task) return await tcs.Task.ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException($"Discord did not answer {cmd} in time");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Discord did not answer {cmd} in time");
            }
            finally { _pending.TryRemove(nonce, out _); }
        }

        private async Task WriteAsync(IpcOpcode op, string json, CancellationToken ct)
        {
            var bytes = IpcCodec.Encode(op, json);
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await _stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally { _writeLock.Release(); }
        }

        private static string? Str(JsonNode? n)
        {
            try { return n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToString(); }
            catch { return null; }
        }

        /// <summary>CLOSE payloads are {"code":..,"message":..}; ERROR replies carry them under "data".</summary>
        internal static (int? Code, string? Message) ReadCodeMessage(string json, bool dataField)
        {
            try
            {
                var node = JsonNode.Parse(json);
                var src = dataField ? node?["data"] : node;
                int? code = null;
                var c = src?["code"];
                if (c is JsonValue cv)
                {
                    if (cv.TryGetValue<int>(out var ci)) code = ci;
                    else if (cv.TryGetValue<string>(out var cs) && int.TryParse(cs, out var cp)) code = cp;
                }
                return (code, Str(src?["message"]));
            }
            catch { return (null, null); }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _cts.Cancel(); } catch { }
            try { _stream.Dispose(); } catch { }
            var reader = _reader;
            if (reader != null)
            {
                try { await Task.WhenAny(reader, Task.Delay(1000)).ConfigureAwait(false); } catch { }
            }
            _closed.TrySetResult(new CloseInfo { Message = "closed by the plugin" });
            FailPending(new CloseInfo { Message = "closed by the plugin" });
            try { _cts.Dispose(); } catch { }
        }
    }
}
