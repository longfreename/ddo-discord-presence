using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DdoDiscordPresence.Discord;

namespace DiscordIpcProbe
{
    /// <summary>
    /// Connects to the local Discord client (\\.\pipe\discord-ipc-0..9) with an application id, performs the handshake,
    /// and prints every frame Discord sends back: the READY user, or the CLOSE / ERROR payload.
    ///
    ///   DiscordIpcProbe &lt;application id&gt; [--redact] [--set "details text"]
    ///
    /// --redact masks the user's name and id in everything printed (use it for anything you paste into docs).
    /// --set sends one SET_ACTIVITY (and clears it 20 s later); without it the probe never changes the user's status.
    /// </summary>
    internal static class Program
    {
        private static bool _redact;

        private static async Task<int> Main(string[] args)
        {
            if (args.Length < 1 || args[0].StartsWith("-", StringComparison.Ordinal))
            {
                Console.WriteLine("usage: DiscordIpcProbe <application id> [--redact] [--set \"details text\"]");
                return 2;
            }
            var clientId = args[0];
            string? set = null;
            for (var i = 1; i < args.Length; i++)
            {
                if (args[i] == "--redact") _redact = true;
                else if (args[i] == "--set" && i + 1 < args.Length) set = args[++i];
            }

            Console.WriteLine($"Application id: {clientId}");
            var connector = new NamedPipeConnector();
            Stream pipe;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                pipe = await connector.ConnectAsync(cts.Token);
            }
            catch (Exception ex)
            {
                Console.WriteLine("RESULT: could not open a Discord pipe: " + ex.Message);
                return 3;
            }
            Console.WriteLine("Pipe opened.");

            await using (pipe)
            {
                var hello = new JsonObject { ["v"] = 1, ["client_id"] = clientId }.ToJsonString();
                Console.WriteLine("-> HANDSHAKE " + hello);
                await pipe.WriteAsync(IpcCodec.Encode(IpcOpcode.Handshake, hello));
                await pipe.FlushAsync();

                using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                while (true)
                {
                    IpcFrame frame;
                    try { frame = await IpcCodec.ReadAsync(pipe, readCts.Token); }
                    catch (OperationCanceledException) { Console.WriteLine("RESULT: no answer within 8 s"); return 4; }
                    catch (EndOfStreamException) { Console.WriteLine("RESULT: Discord closed the pipe without a frame"); return 5; }
                    Console.WriteLine($"<- {frame.Opcode} {Redact(frame.Json)}");
                    if (frame.Opcode == IpcOpcode.Ping)
                    {
                        await pipe.WriteAsync(IpcCodec.Encode(IpcOpcode.Pong, frame.Json));
                        continue;
                    }
                    if (frame.Opcode == IpcOpcode.Close)
                    {
                        var (code, message) = DiscordRpcConnection.ReadCodeMessage(frame.Json, dataField: false);
                        Console.WriteLine($"RESULT: Discord refused the handshake: code {code?.ToString() ?? "-"}, message \"{message}\"");
                        return 1;
                    }
                    if (frame.Opcode != IpcOpcode.Frame) continue;
                    var node = JsonNode.Parse(frame.Json);
                    if (node?["evt"]?.GetValue<string>() == "READY")
                    {
                        var user = node["data"]?["user"];
                        var name = user?["global_name"]?.ToString() ?? user?["username"]?.ToString() ?? "?";
                        Console.WriteLine("RESULT: READY, connected as " + (_redact ? "<redacted>" : name));
                        break;
                    }
                    if (node?["evt"]?.GetValue<string>() == "ERROR")
                    {
                        Console.WriteLine("RESULT: Discord answered with an ERROR event");
                        return 1;
                    }
                }
            }

            if (set == null) return 0;

            // Optional: one real SET_ACTIVITY through the plugin's connection class, cleared again after 20 s.
            Console.WriteLine("Sending one SET_ACTIVITY (cleared again in 20 s)...");
            await using var conn = await DiscordRpcConnection.OpenAsync(new NamedPipeConnector(), clientId, TimeSpan.FromSeconds(5), CancellationToken.None);
            var reply = await conn.SetActivityAsync(Environment.ProcessId, new JsonObject
            {
                ["details"] = set,
                ["state"] = "DiscordIpcProbe test",
                ["timestamps"] = new JsonObject { ["start"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
                ["assets"] = new JsonObject { ["large_image"] = "ddo", ["large_text"] = "Dungeons & Dragons Online" },
            }, TimeSpan.FromSeconds(5), CancellationToken.None);
            Console.WriteLine("<- " + Redact(reply.Raw));
            Console.WriteLine("RESULT: SET_ACTIVITY " + reply.Describe());
            await Task.Delay(TimeSpan.FromSeconds(20));
            var cleared = await conn.SetActivityAsync(Environment.ProcessId, null, TimeSpan.FromSeconds(5), CancellationToken.None);
            Console.WriteLine("Cleared: " + cleared.Describe());
            return reply.Ok ? 0 : 1;
        }

        private static readonly Regex UserFields = new Regex("\"(id|username|global_name|avatar|discriminator|avatar_decoration_data|clan|primary_guild|premium_type|flags|bot)\":(\"[^\"]*\"|null|true|false|\\d+|\\{[^{}]*\\})", RegexOptions.CultureInvariant);

        private static string Redact(string json) => _redact ? UserFields.Replace(json, m => $"\"{m.Groups[1].Value}\":\"<redacted>\"") : json;
    }
}
