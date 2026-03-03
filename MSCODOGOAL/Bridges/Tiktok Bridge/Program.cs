#nullable disable

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TikTokLiveSharp.Client;
using TikTokLiveSharp.Events;

namespace MSCODOGOALTIKTOK_Bridge
{
    internal class Program
    {
        // =========================
        // Local WS server (the mod connects to this)
        // =========================
        private static readonly ConcurrentDictionary<Guid, WebSocket> Clients = new();

        private static string BaseDir => AppDomain.CurrentDomain.BaseDirectory;

        // IMPORTANT: separate config name so users don’t mix them up
        private static string ConfigPath => Path.Combine(BaseDir, "tiktokconfig.json");
        private static string LogPath => Path.Combine(BaseDir, "tiktok_bridge.log");

        private class Config
        {
            public int local_ws_port = 8766;

            public bool tiktok_enabled = true;
            public string tiktok_username = ""; // WITHOUT @

            public bool enable_gifts = true;

            // Conversion: diamonds -> USD (editable because it varies / fees / region)
            public double usd_per_diamond = 0.005;

            // Optional: ignore very small gifts (anti-spam)
            public double min_usd_to_forward = 0.00;

            // Diagnostics / UX
            public bool verbose_tiktok_logging = false;
            public bool open_config_on_first_run = true;

            // Status messages to mod
            public bool enable_status_heartbeat = false;
            public int status_heartbeat_seconds = 5;

            // Logging controls
            public bool log_broadcast_events = true;
            public bool log_broadcast_status = false;

            // Retry tuning
            public int retry_seconds_general = 10;
            public int retry_seconds_roomid = 30;
        }

        private static Config _cfg = new();
        private static StreamWriter _logWriter;

        private static TikTokLiveClient _tt;
        private static bool _tiktokConnected = false;
        private static DateTime _tiktokLastEventUtc = DateTime.MinValue;
        private static string _tiktokLastEventType = "";
        private static string _tiktokLastError = "";

        private static bool _firstRunCreatedConfig = false;

        // Dedupe
        private static readonly ConcurrentDictionary<string, DateTime> _seen = new();
        private static DateTime _lastDedupeCleanupUtc = DateTime.MinValue;

        // Backoff control (prevents spam)
        private static DateTime _nextTikTokStartAttemptUtc = DateTime.MinValue;

        private static async Task Main(string[] args)
        {
            Console.Title = "MSCODOGOALTIKTOK Bridge";

            _cfg = LoadOrCreateConfig(out _firstRunCreatedConfig);

            try { _logWriter = new StreamWriter(LogPath, append: true) { AutoFlush = true }; }
            catch { _logWriter = null; }

            Log("========================================");
            Log("MSCODOGOALTIKTOK Bridge starting...");
            Log("BaseDir: " + BaseDir);

            bool fixedAnything = ValidateAndFixConfig(_cfg);
            if (fixedAnything)
            {
                try { SaveConfig(_cfg); Log("Config auto-fixed and saved."); }
                catch (Exception ex) { Log("WARNING: Failed to save auto-fixed config: " + ex.Message); }
            }

            if (_firstRunCreatedConfig)
            {
                Log("Created tiktokconfig.json (first run).");
                PrintSetupInstructions();
                if (_cfg.open_config_on_first_run) TryOpenConfigFile();
            }

            using var cts = new CancellationTokenSource();

            var serverTask = RunWebSocketServerAsync(_cfg.local_ws_port, cts.Token);
            var tiktokTask = RunTikTokLoopAsync(cts.Token);
            var heartbeatTask = RunStatusHeartbeatAsync(cts.Token);

            await RunCommandLoopAsync(cts);

            cts.Cancel();

            try { await serverTask; } catch { }
            try { await tiktokTask; } catch { }
            try { await heartbeatTask; } catch { }

            await StopTikTokAsync();

            Log("Bridge stopped.");
            try { _logWriter?.Dispose(); } catch { }
        }

        // =========================
        // Config
        // =========================
        private static bool ValidateAndFixConfig(Config cfg)
        {
            bool changed = false;
            if (cfg == null) return false;

            if (cfg.local_ws_port < 1 || cfg.local_ws_port > 65535)
            {
                Log("WARNING: local_ws_port out of range. Auto-fixing to 8766");
                cfg.local_ws_port = 8766;
                changed = true;
            }

            if (cfg.enable_status_heartbeat && cfg.status_heartbeat_seconds <= 0)
            {
                Log("WARNING: status_heartbeat_seconds <= 0. Auto-fixing to 5");
                cfg.status_heartbeat_seconds = 5;
                changed = true;
            }

            if (cfg.usd_per_diamond <= 0.0)
            {
                Log("WARNING: usd_per_diamond <= 0. Auto-fixing to 0.005");
                cfg.usd_per_diamond = 0.005;
                changed = true;
            }

            if (cfg.tiktok_username == null) cfg.tiktok_username = "";
            cfg.tiktok_username = cfg.tiktok_username.Trim();
            if (cfg.tiktok_username.StartsWith("@")) cfg.tiktok_username = cfg.tiktok_username.Substring(1);

            if (cfg.min_usd_to_forward < 0.0) cfg.min_usd_to_forward = 0.0;

            if (cfg.retry_seconds_general < 3) cfg.retry_seconds_general = 10;
            if (cfg.retry_seconds_roomid < 5) cfg.retry_seconds_roomid = 30;

            return changed;
        }

        private static void SaveConfig(Config cfg)
        {
            var json = JsonConvert.SerializeObject(cfg, Formatting.Indented);
            File.WriteAllText(ConfigPath, json + Environment.NewLine);
        }

        private static Config LoadOrCreateConfig(out bool created)
        {
            created = false;

            if (!File.Exists(ConfigPath))
            {
                var def = new Config();
                File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(def, Formatting.Indented) + Environment.NewLine);
                created = true;
                return def;
            }

            try
            {
                string txt = File.ReadAllText(ConfigPath);
                var loaded = JsonConvert.DeserializeObject<Config>(txt);
                return loaded ?? new Config();
            }
            catch
            {
                Console.WriteLine("[BRIDGE] Failed to parse tiktokconfig.json, using defaults.");
                return new Config();
            }
        }

        // =========================
        // Command loop
        // =========================
        private static async Task RunCommandLoopAsync(CancellationTokenSource cts)
        {
            PrintHelp();

            while (!cts.IsCancellationRequested)
            {
                string line = Console.ReadLine();
                if (line == null) continue;
                line = line.Trim();
                if (line.Length == 0) continue;

                if (line.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
                    line.Equals("exit", StringComparison.OrdinalIgnoreCase))
                    return;

                if (line.Equals("help", StringComparison.OrdinalIgnoreCase) ||
                    line.Equals("?", StringComparison.OrdinalIgnoreCase))
                {
                    PrintHelp();
                    continue;
                }

                if (line.Equals("clients", StringComparison.OrdinalIgnoreCase))
                {
                    Log("Clients connected: " + Clients.Count);
                    continue;
                }

                if (line.Equals("status", StringComparison.OrdinalIgnoreCase))
                {
                    await BroadcastStatusAsync();
                    continue;
                }

                if (line.Equals("tt status", StringComparison.OrdinalIgnoreCase))
                {
                    Log("TikTok: " + (_tiktokConnected ? "CONNECTED" : "DISCONNECTED") +
                        (string.IsNullOrEmpty(_tiktokLastError) ? "" : (" | LastError=" + _tiktokLastError)));
                    continue;
                }

                if (line.Equals("tt reconnect", StringComparison.OrdinalIgnoreCase))
                {
                    Log("TikTok: manual reconnect requested.");
                    _nextTikTokStartAttemptUtc = DateTime.MinValue;
                    await RestartTikTokAsync(cts.Token);
                    continue;
                }

                if (line.Equals("tt start", StringComparison.OrdinalIgnoreCase))
                {
                    Log("TikTok: manual start requested.");
                    _nextTikTokStartAttemptUtc = DateTime.MinValue;
                    await RestartTikTokAsync(cts.Token);
                    continue;
                }

                if (line.StartsWith("test ", StringComparison.OrdinalIgnoreCase))
                {
                    // test 5  -> forwards $5 as donation packet
                    var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double amount))
                    {
                        string key = "manual:" + Guid.NewGuid().ToString("N");
                        await ForwardDonationToModAsync(amount, "TESTUSER", "manual_test", key);
                    }
                    else Log("Usage: test 5");
                    continue;
                }

                Log("Unknown command. Type 'help'.");
            }
        }

        private static void PrintHelp()
        {
            Log("Ready. Commands:");
            Log("  help / ?         -> show commands");
            Log("  clients          -> show connected mod clients");
            Log("  status           -> broadcast status to mod");
            Log("  test 5           -> sends donation $5 to mod");
            Log("  tt status        -> show TikTok connection state");
            Log("  tt reconnect     -> reconnect TikTok");
            Log("  tt start         -> start TikTok client (manual)");
            Log("  quit             -> exit");
        }

        private static void PrintSetupInstructions()
        {
            Log("SETUP:");
            Log("1) Open tiktokconfig.json and set your TikTok username (WITHOUT @):");
            Log("   tiktok_username: \"yourname\"");
            Log("2) Go LIVE on TikTok (this bridge listens to LIVE only).");
            Log("3) Restart TikTokBridge.exe");
            Log("4) Start My Summer Car (mod connects automatically)");
        }

        private static void TryOpenConfigFile()
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = ConfigPath, UseShellExecute = true });
                Log("Opened tiktokconfig.json.");
            }
            catch { }
        }

        // =========================
        // Local WebSocket server
        // =========================
        private static async Task RunWebSocketServerAsync(int port, CancellationToken ct)
        {
            string prefix = "http://127.0.0.1:" + port + "/";

            using var listener = new HttpListener();
            listener.Prefixes.Add(prefix);

            try { listener.Start(); }
            catch (HttpListenerException ex)
            {
                Log("ERROR: Failed to start listener on " + prefix);
                Log("HttpListenerException: " + ex.Message);
                throw;
            }

            Log("Local WS server listening at ws://127.0.0.1:" + port + "/");

            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx = null;
                try { ctx = await listener.GetContextAsync(); }
                catch when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Log("Listener error: " + ex.Message);
                    continue;
                }

                if (!ctx.Request.IsWebSocketRequest)
                {
                    ctx.Response.StatusCode = 400;
                    ctx.Response.Close();
                    continue;
                }

                _ = Task.Run(() => HandleClientAsync(ctx), ct);
            }

            listener.Stop();
        }

        private static async Task HandleClientAsync(HttpListenerContext ctx)
        {
            WebSocketContext wsCtx;
            try { wsCtx = await ctx.AcceptWebSocketAsync(null); }
            catch (Exception ex)
            {
                Log("WS accept failed: " + ex.Message);
                try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
                return;
            }

            var socket = wsCtx.WebSocket;
            var id = Guid.NewGuid();
            Clients.TryAdd(id, socket);

            Log("Client connected. Count=" + Clients.Count);

            await SendJsonAsync(socket, new { type = "hello", msg = "bridge_connected" });
            await SendStatusAsync(socket);
            await BroadcastStatusAsync();

            var buffer = new byte[8192];

            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;
                }
            }
            catch (Exception ex)
            {
                Log("Client recv error: " + ex.Message);
            }
            finally
            {
                Clients.TryRemove(id, out _);

                try
                {
                    if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
                }
                catch { }

                Log("Client disconnected. Count=" + Clients.Count);
                await BroadcastStatusAsync();
            }
        }

        private static async Task BroadcastJsonAsync(object payload, bool logThisBroadcast)
        {
            string json = JsonConvert.SerializeObject(payload);

            int sent = 0;
            foreach (var kvp in Clients)
            {
                var ws = kvp.Value;
                if (ws.State != WebSocketState.Open) continue;

                try { await SendTextAsync(ws, json); sent++; }
                catch { }
            }

            if (logThisBroadcast)
                Log("Broadcast to " + sent + " client(s): " + json);
        }

        private static Task SendTextAsync(WebSocket ws, string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            return ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private static Task SendJsonAsync(WebSocket ws, object payload)
        {
            string json = JsonConvert.SerializeObject(payload);
            return SendTextAsync(ws, json);
        }

        // =========================
        // Status packets to mod (TikTok-specific)
        // =========================
        private static async Task RunStatusHeartbeatAsync(CancellationToken ct)
        {
            if (!_cfg.enable_status_heartbeat)
                return;

            int sec = _cfg.status_heartbeat_seconds;
            if (sec <= 0) sec = 5;

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(sec * 1000, ct).ContinueWith(_ => { });
                if (ct.IsCancellationRequested) break;
                await BroadcastStatusAsync();
            }
        }

        private static async Task BroadcastStatusAsync()
        {
            await BroadcastJsonAsync(new
            {
                type = "status",
                bridge = "ok",
                bridge_kind = "tiktok",

                local_ws_port = _cfg.local_ws_port,
                clients = Clients.Count,

                tiktok_connected = _tiktokConnected,
                tiktok_last_event_utc = (_tiktokLastEventUtc == DateTime.MinValue) ? "" : _tiktokLastEventUtc.ToString("u"),
                tiktok_last_event_type = _tiktokLastEventType ?? "",
                tiktok_last_error = _tiktokLastError ?? ""
            }, _cfg.log_broadcast_status);
        }

        private static Task SendStatusAsync(WebSocket ws)
        {
            return SendJsonAsync(ws, new
            {
                type = "status",
                bridge = "ok",
                bridge_kind = "tiktok",

                local_ws_port = _cfg.local_ws_port,
                clients = Clients.Count,

                tiktok_connected = _tiktokConnected,
                tiktok_last_event_utc = (_tiktokLastEventUtc == DateTime.MinValue) ? "" : _tiktokLastEventUtc.ToString("u"),
                tiktok_last_event_type = _tiktokLastEventType ?? "",
                tiktok_last_error = _tiktokLastError ?? ""
            });
        }

        // =========================
        // TikTok loop (with spam-safe backoff)
        // =========================
        private static async Task RunTikTokLoopAsync(CancellationToken ct)
        {
            if (!_cfg.tiktok_enabled)
            {
                Log("TikTok disabled (tiktok_enabled=false).");
                return;
            }

            if (string.IsNullOrWhiteSpace(_cfg.tiktok_username))
            {
                Log("TikTok username missing. Put it into tiktokconfig.json then restart Bridge.");
                return;
            }

            // First attempt immediately
            _nextTikTokStartAttemptUtc = DateTime.MinValue;

            while (!ct.IsCancellationRequested)
            {
                // If connected, just idle and let events flow.
                if (_tiktokConnected)
                {
                    await Task.Delay(1000, ct).ContinueWith(_ => { });
                    continue;
                }

                // Not connected: respect backoff
                var now = DateTime.UtcNow;
                if (now < _nextTikTokStartAttemptUtc)
                {
                    await Task.Delay(500, ct).ContinueWith(_ => { });
                    continue;
                }

                await RestartTikTokAsync(ct);
                await BroadcastStatusAsync();

                // Set next attempt depending on last error type
                int delaySec = _cfg.retry_seconds_general;
                if (!string.IsNullOrEmpty(_tiktokLastError) &&
                    (_tiktokLastError.IndexOf("RoomId", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     _tiktokLastError.IndexOf("blocked", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    delaySec = _cfg.retry_seconds_roomid;
                }

                _nextTikTokStartAttemptUtc = DateTime.UtcNow.AddSeconds(Math.Max(3, delaySec));

                await Task.Delay(500, ct).ContinueWith(_ => { });
            }
        }

        private static async Task RestartTikTokAsync(CancellationToken ct)
        {
            await StopTikTokAsync();
            await StartTikTokAsync(ct);
        }

        private static async Task StartTikTokAsync(CancellationToken ct)
        {
            try
            {
                _tiktokLastError = "";
                _tiktokLastEventType = "";
                _tiktokConnected = false;

                _tt = new TikTokLiveClient(_cfg.tiktok_username, "");

                _tt.OnConnected += (sender, connected) =>
                {
                    _tiktokConnected = connected;
                    _tiktokLastError = "";
                    Log("TikTok: CONNECTED");
                    _ = BroadcastStatusAsync();
                };

                _tt.OnDisconnected += (sender, connected) =>
                {
                    _tiktokConnected = false;
                    Log("TikTok: DISCONNECTED");
                    _ = BroadcastStatusAsync();
                };

                _tt.OnGiftMessage += async (sender, e) =>
                {
                    if (!_cfg.enable_gifts) return;

                    try
                    {
                        _tiktokLastEventUtc = DateTime.UtcNow;
                        _tiktokLastEventType = "tiktok:gift";
                        _tiktokLastError = "";

                        string username = e?.User?.UniqueId ?? "unknown";
                        string giftName = e?.Gift?.Name ?? "";
                        int diamondCost = e?.Gift?.DiamondCost ?? 0;

                        // Prevent streak spam:
                        bool streakable = e?.Gift?.IsStreakable ?? false;
                        bool streakEnd = e?.StreakEnd ?? true; // if missing, treat as end

                        if (streakable && !streakEnd)
                        {
                            if (_cfg.verbose_tiktok_logging)
                                Log($"TikTok gift streak in-progress ignored: {username} {e.Amount}x {giftName}");
                            return;
                        }

                        long amount = (e?.Amount ?? 0);
                        if (amount <= 0) amount = 1;

                        // Diamonds -> USD:
                        double diamonds = diamondCost * (double)amount;
                        double usd = diamonds * _cfg.usd_per_diamond;

                        if (usd < _cfg.min_usd_to_forward)
                            return;

                        string dedupeKey =
                            $"ttgift:{username}|{giftName}|dc{diamondCost}|amt{amount}|end{streakEnd}";

                        if (_cfg.verbose_tiktok_logging)
                            Log($"Gift: {username} sent {amount}x {giftName} (diamondCost={diamondCost}) => diamonds={diamonds} usd={usd:F4}");

                        await ForwardDonationToModAsync(usd, username, "tiktok_gift", dedupeKey);
                        await BroadcastStatusAsync();
                    }
                    catch (Exception ex)
                    {
                        _tiktokLastError = ex.Message;
                        Log("TikTok gift handler error: " + ex.Message);
                        _ = BroadcastStatusAsync();
                    }
                };

                // RunAsync exists; run in background so bridge stays responsive
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _tt.RunAsync(ct);
                    }
                    catch (Exception ex)
                    {
                        _tiktokConnected = false;
                        _tiktokLastError = ex.Message ?? "Unknown error";

                        if (_tiktokLastError.IndexOf("RoomId", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            // Friendly message for the exact error you got
                            Log("TikTok: NOT LIVE / RoomId not found. (This is normal if your account isn't LIVE or not eligible yet.)");
                        }
                        else
                        {
                            Log("TikTok loop error: " + _tiktokLastError);
                        }

                        _ = BroadcastStatusAsync();
                    }
                }, ct);

                Log("TikTok: starting...");
            }
            catch (Exception ex)
            {
                _tiktokConnected = false;
                _tiktokLastError = ex.Message ?? "Unknown error";

                if (_tiktokLastError.IndexOf("RoomId", StringComparison.OrdinalIgnoreCase) >= 0)
                    Log("TikTok: NOT LIVE / RoomId not found. (This is normal if your account isn't LIVE or not eligible yet.)");
                else
                    Log("TikTok start error: " + _tiktokLastError);
            }
        }

        private static Task StopTikTokAsync()
        {
            try
            {
                if (_tt != null)
                {
                    // TikTokLiveClient in 1.2.3 may or may not be IDisposable depending on package build,
                    // so we guard it safely.
                    try
                    {
                        if (_tt is IDisposable d) d.Dispose();
                    }
                    catch { }

                    _tt = null;
                }
            }
            catch { }

            _tiktokConnected = false;
            return Task.CompletedTask;
        }

        // =========================
        // Donation forwarding + dedupe
        // =========================
        private static async Task ForwardDonationToModAsync(double amount, string username, string source, string dedupeKey)
        {
            if (amount <= 0.0) return;

            if (ShouldSkipAsDuplicate(dedupeKey))
            {
                if (_cfg.verbose_tiktok_logging)
                    Log("Skipped duplicate: " + dedupeKey);
                return;
            }

            await BroadcastJsonAsync(new
            {
                type = "donation",
                amount = amount,
                username = username,
                source = source
            }, _cfg.log_broadcast_events);
        }

        private static bool ShouldSkipAsDuplicate(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return false;

            var now = DateTime.UtcNow;
            var ttl = TimeSpan.FromMinutes(5);

            if ((now - _lastDedupeCleanupUtc) > TimeSpan.FromSeconds(30))
            {
                _lastDedupeCleanupUtc = now;
                foreach (var kv in _seen)
                {
                    if ((now - kv.Value) > ttl)
                        _seen.TryRemove(kv.Key, out _);
                }
            }

            return !_seen.TryAdd(key, now);
        }

        // =========================
        // Logging
        // =========================
        private static void Log(string msg)
        {
            string line = "[BRIDGE] " + msg;
            Console.WriteLine(line);
            try { _logWriter?.WriteLine(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC' ") + line); } catch { }
        }
    }
}