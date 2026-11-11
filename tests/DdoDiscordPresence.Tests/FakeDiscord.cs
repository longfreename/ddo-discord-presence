using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DdoDiscordPresence.Discord;

namespace DdoDiscordPresence.Tests
{
    /// <summary>
    /// An in-process stand-in for the Discord client: a named pipe server with a unique test name that speaks the RPC
    /// framing. It records every frame it receives and answers HANDSHAKE / SET_ACTIVITY the way it is told to.
    /// </summary>
    internal sealed class FakeDiscord : IAsyncDisposable
    {
        public enum HandshakeMode { Ready, CloseInvalidClientId, Silent }

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private NamedPipeServerStream? _pipe;
        private readonly SemaphoreSlim _write = new SemaphoreSlim(1, 1);
        private Task? _serve;

        public FakeDiscord(string? prefix = null)
        {
            Prefix = prefix ?? "ddp-test-" + Guid.NewGuid().ToString("N").Substring(0, 12) + "-";
        }

        /// <summary>The pipe is {Prefix}0; give the connector the same prefix with count 1.</summary>
        public string Prefix { get; }
        public HandshakeMode Mode { get; set; } = HandshakeMode.Ready;
        /// <summary>When set, SET_ACTIVITY is answered with this ERROR (code, message).</summary>
        public (int Code, string Message)? ActivityError { get; set; }
        public bool AnswerCommands { get; set; } = true;
        public string UserName { get; set; } = "tester";

        public readonly ConcurrentQueue<IpcFrame> Received = new ConcurrentQueue<IpcFrame>();
        public readonly ConcurrentQueue<JsonNode> Activities = new ConcurrentQueue<JsonNode>();   // args of each SET_ACTIVITY
        public int Connections;
        public int SetActivityCount;
        public string? LastClientId;
        public readonly TaskCompletionSource<string> PongReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        public IPipeConnector Connector(int perPipeTimeoutMs = 500) => new NamedPipeConnector(Prefix, 1, TimeSpan.FromMilliseconds(perPipeTimeoutMs));

        /// <summary>Starts listening; serves one client at a time, again after each disconnect, until disposed.</summary>
        public FakeDiscord Start()
        {
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _serve = Task.Run(() => ServeAsync(ready));
            ready.Task.Wait(2000);
            return this;
        }

        private async Task ServeAsync(TaskCompletionSource<bool> ready)
        {
            while (!_cts.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(Prefix + "0", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                _pipe = pipe;
                ready.TrySetResult(true);
                try
                {
                    await pipe.WaitForConnectionAsync(_cts.Token);
                    Interlocked.Increment(ref Connections);
                    await HandleAsync(pipe, _cts.Token);
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                finally { try { pipe.Dispose(); } catch { } }
            }
        }

        private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken ct)
        {
            var hello = await IpcCodec.ReadAsync(pipe, ct);
            Received.Enqueue(hello);
            LastClientId = JsonNode.Parse(hello.Json)?["client_id"]?.GetValue<string>();
            switch (Mode)
            {
                case HandshakeMode.CloseInvalidClientId:
                    await SendAsync(pipe, IpcOpcode.Close, "{\"code\":4000,\"message\":\"Invalid Client ID\"}", ct);
                    await Task.Delay(100, ct);
                    return;
                case HandshakeMode.Silent:
                    await Task.Delay(Timeout.Infinite, ct);
                    return;
            }
            var ready = new JsonObject
            {
                ["cmd"] = "DISPATCH",
                ["data"] = new JsonObject
                {
                    ["v"] = 1,
                    ["config"] = new JsonObject { ["cdn_host"] = "cdn.example", ["api_endpoint"] = "//api.example", ["environment"] = "test" },
                    ["user"] = new JsonObject { ["id"] = "123456789012345678", ["username"] = UserName, ["global_name"] = null, ["discriminator"] = "0" },
                },
                ["evt"] = "READY",
                ["nonce"] = null,
            };
            await SendAsync(pipe, IpcOpcode.Frame, ready.ToJsonString(), ct);
            while (!ct.IsCancellationRequested)
            {
                var frame = await IpcCodec.ReadAsync(pipe, ct);
                Received.Enqueue(frame);
                if (frame.Opcode == IpcOpcode.Pong) { PongReceived.TrySetResult(frame.Json); continue; }
                if (frame.Opcode != IpcOpcode.Frame) continue;
                var node = JsonNode.Parse(frame.Json)!;
                if (node["cmd"]?.GetValue<string>() != "SET_ACTIVITY") continue;
                Interlocked.Increment(ref SetActivityCount);
                Activities.Enqueue(node["args"]!.DeepClone());
                if (!AnswerCommands) continue;
                var nonce = node["nonce"]?.GetValue<string>();
                JsonObject reply;
                if (ActivityError is (int code, string msg))
                    reply = new JsonObject { ["cmd"] = "SET_ACTIVITY", ["data"] = new JsonObject { ["code"] = code, ["message"] = msg }, ["evt"] = "ERROR", ["nonce"] = nonce };
                else
                    reply = new JsonObject { ["cmd"] = "SET_ACTIVITY", ["data"] = node["args"]?["activity"]?.DeepClone(), ["evt"] = null, ["nonce"] = nonce };
                await SendAsync(pipe, IpcOpcode.Frame, reply.ToJsonString(), ct);
            }
        }

        private async Task SendAsync(Stream pipe, IpcOpcode op, string json, CancellationToken ct)
        {
            var bytes = IpcCodec.Encode(op, json);
            await _write.WaitAsync(ct);
            try { await pipe.WriteAsync(bytes, ct); await pipe.FlushAsync(ct); }
            finally { _write.Release(); }
        }

        public Task SendPingAsync(string json) => _pipe != null ? SendAsync(_pipe, IpcOpcode.Ping, json, _cts.Token) : Task.CompletedTask;
        public Task SendCloseAsync(int code, string message) =>
            _pipe != null ? SendAsync(_pipe, IpcOpcode.Close, new JsonObject { ["code"] = code, ["message"] = message }.ToJsonString(), _cts.Token) : Task.CompletedTask;

        /// <summary>Breaks the current connection (as when Discord quits); the server listens again afterwards.</summary>
        public void DropClient()
        {
            try { _pipe?.Dispose(); } catch { }
        }

        public List<JsonNode> ActivityList() => new List<JsonNode>(Activities);

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _pipe?.Dispose(); } catch { }
            if (_serve != null) { try { await Task.WhenAny(_serve, Task.Delay(2000)); } catch { } }
        }
    }
}
