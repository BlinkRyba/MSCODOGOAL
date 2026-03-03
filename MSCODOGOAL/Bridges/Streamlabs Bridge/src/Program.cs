#nullable disable

using System;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SocketIOClient;
using SocketIOClient.Common;

namespace MSCODOGOALTWITCH_Bridge
{
    internal class Program
    {
        // =========================
        // State
        // =========================
        private static readonly ConcurrentDictionary<Guid, WebSocket> Clients = new ConcurrentDictionary<Guid, WebSocket>();

        private static string BaseDir => AppDomain.CurrentDomain.BaseDirectory;
        private static string ConfigPath => Path.Combine(BaseDir, "config.json");
        private static string LogPath => Path.Combine(BaseDir, "bridge.log");

        private class Config
        {
            // Local WebSocket server (the mod connects to this)
            public int local_ws_port = 8765;

            // Streamlabs Socket API
            public bool streamlabs_enabled = true;
            public string streamlabs_socket_token = "";

            // Forward which events
            public bool enable_tips = true;
            public bool enable_bits = true;
            public bool enable_subscriptions = true;

            // Conversions
            public double subscription_value_usd = 5.00;
            public double bits_per_usd = 100.0; // 100 bits = $1.00

            // Diagnostics / UX
            public bool verbose_streamlabs_logging = false;
            public bool open_config_on_first_run = true;

            // Status messages to mod
            public bool enable_status_heartbeat = false;
            public int status_heartbeat_seconds = 5;

            // Logging controls (reduce spam)
            public bool log_broadcast_events = true;  // donations/bits/subs/test/sltest
            public bool log_broadcast_status = false; // heartbeat/status packets
        }

        private static Config _cfg = new Config();
        private static StreamWriter _logWriter = null;

        private static SocketIO _streamlabs = null;

        private static bool _streamlabsConnected = false;
        private static DateTime _streamlabsLastEventUtc = DateTime.MinValue;
        private static string _streamlabsLastEventType = "";
        private static string _streamlabsLastError = "";

        private static bool _firstRunCreatedConfig = false;

        // =========================
        // Dedupe
        // =========================
        private static readonly ConcurrentDictionary<string, DateTime> _seen = new ConcurrentDictionary<string, DateTime>();
        private static DateTime _lastDedupeCleanupUtc = DateTime.MinValue;

        // =========================
        // Main
        // =========================
        private static async Task Main(string[] args)
        {
            Console.Title = "MSCODOGOALTWITCH Bridge";

            _cfg = LoadOrCreateConfig(out _firstRunCreatedConfig);

            try { _logWriter = new StreamWriter(LogPath, append: true) { AutoFlush = true }; }
            catch { _logWriter = null; }

            Log("========================================");
            Log("MSCODOGOALTWITCH Bridge starting...");
            Log("BaseDir: " + BaseDir);

            // Auto-fix config values (and save if needed)
            bool fixedAnything = ValidateAndFixConfig(_cfg);
            if (fixedAnything)
            {
                try
                {
                    SaveConfig(_cfg);
                    Log("Config auto-fixed and saved.");
                }
                catch (Exception ex)
                {
                    Log("WARNING: Failed to save auto-fixed config: " + ex.Message);
                }
            }

            if (_firstRunCreatedConfig)
            {
                Log("Created config.json (first run).");
                PrintSetupInstructions();
                if (_cfg.open_config_on_first_run)
                    TryOpenConfigFile();
            }

            using (var cts = new CancellationTokenSource())
            {
                var serverTask = RunWebSocketServerAsync(_cfg.local_ws_port, cts.Token);
                var streamlabsTask = RunStreamlabsLoopAsync(cts.Token);
                var heartbeatTask = RunStatusHeartbeatAsync(cts.Token);

                await RunCommandLoopAsync(cts);

                cts.Cancel();
                try { await serverTask; } catch { }
                try { await streamlabsTask; } catch { }
                try { await heartbeatTask; } catch { }

                await StopStreamlabsAsync();
            }

            Log("Bridge stopped.");
            try { _logWriter?.Dispose(); } catch { }
        }

        // =========================
        // Config validation + save
        // =========================
        private static bool ValidateAndFixConfig(Config cfg)
        {
            bool changed = false;
            if (cfg == null) return false;

            // If bits_per_usd is invalid, default it to 100.0
            if (cfg.bits_per_usd <= 0.0)
            {
                Log("WARNING: bits_per_usd was <= 0. Auto-fixing to 100.0");
                cfg.bits_per_usd = 100.0;
                changed = true;
            }

            // (Optional tiny safety) heartbeat seconds shouldn't be <= 0 if heartbeat enabled
            if (cfg.enable_status_heartbeat && cfg.status_heartbeat_seconds <= 0)
            {
                Log("WARNING: status_heartbeat_seconds was <= 0 while heartbeat enabled. Auto-fixing to 5");
                cfg.status_heartbeat_seconds = 5;
                changed = true;
            }

            // (Optional tiny safety) local port should be in valid range
            if (cfg.local_ws_port < 1 || cfg.local_ws_port > 65535)
            {
                Log("WARNING: local_ws_port out of range. Auto-fixing to 8765");
                cfg.local_ws_port = 8765;
                changed = true;
            }

            return changed;
        }

        private static void SaveConfig(Config cfg)
        {
            // Simple safe-ish write (you can upgrade to atomic temp file if you want)
            var json = JsonConvert.SerializeObject(cfg, Formatting.Indented);
            File.WriteAllText(ConfigPath, json + Environment.NewLine);
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

                if (line.Equals("sl status", StringComparison.OrdinalIgnoreCase))
                {
                    Log("Streamlabs: " + (_streamlabs == null ? "not initialized" : (_streamlabs.Connected ? "CONNECTED" : "DISCONNECTED")));
                    continue;
                }

                if (line.Equals("sl reconnect", StringComparison.OrdinalIgnoreCase))
                {
                    Log("Streamlabs: manual reconnect requested.");
                    await RestartStreamlabsAsync();
                    continue;
                }

                if (line.StartsWith("test ", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && double.TryParse(parts[1], out double amount))
                    {
                        string key = "manual:" + Guid.NewGuid().ToString("N");
                        await ForwardDonationToModAsync(amount, "TESTUSER", "manual_test", key);
                    }
                    else Log("Usage: test 5");
                    continue;
                }

                if (line.StartsWith("sltest ", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleSlTestCommand(line);
                    continue;
                }

                Log("Unknown command. Type 'help'.");
            }
        }

        private static void PrintHelp()
        {
            Log("Ready. Commands:");
            Log("  help / ?             -> show commands");
            Log("  clients              -> show connected mod clients");
            Log("  status               -> broadcast status to mod");
            Log("  test 5               -> sends donation $5 to mod");
            Log("  sl status            -> show Streamlabs connection state");
            Log("  sl reconnect         -> reconnect Streamlabs");
            Log("  sltest tip 5 name    -> simulate Streamlabs tip");
            Log("  sltest bits 300 name -> simulate Twitch bits via Streamlabs");
            Log("  sltest sub name      -> simulate Twitch sub via Streamlabs");
            Log("  quit                 -> exit");
        }

        private static void PrintSetupInstructions()
        {
            Log("SETUP:");
            Log("1) Open config.json and paste your Streamlabs socket token:");
            Log("   streamlabs_socket_token: \"...\"");
            Log("2) Restart Bridge.exe");
            Log("3) Start My Summer Car (mod connects automatically)");
        }

        private static void TryOpenConfigFile()
        {
            try
            {
                var psi = new ProcessStartInfo { FileName = ConfigPath, UseShellExecute = true };
                Process.Start(psi);
                Log("Opened config.json.");
            }
            catch { }
        }

        private static async Task HandleSlTestCommand(string line)
        {
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                Log("Usage: sltest tip 5 name | sltest bits 300 name | sltest sub name");
                return;
            }

            string kind = parts[1].ToLowerInvariant();

            if (kind == "tip" && parts.Length >= 4)
            {
                if (double.TryParse(parts[2], out double amount))
                {
                    string username = parts[3];
                    string key = "sltest_tip:" + Guid.NewGuid().ToString("N");
                    await ForwardDonationToModAsync(amount, username, "sltest_tip", key);
                }
                else Log("Usage: sltest tip 5 name");
                return;
            }

            if (kind == "bits" && parts.Length >= 4)
            {
                if (double.TryParse(parts[2], out double bits))
                {
                    string username = parts[3];

                    // Always convert using bits_per_usd
                    double usd = (_cfg.bits_per_usd <= 0.0) ? 0.0 : (bits / _cfg.bits_per_usd);

                    string key = "sltest_bits:" + Guid.NewGuid().ToString("N");
                    await ForwardDonationToModAsync(usd, username, "sltest_bits", key);
                }
                else Log("Usage: sltest bits 300 name");
                return;
            }

            if (kind == "sub" && parts.Length >= 3)
            {
                string username = parts[2];
                string key = "sltest_sub:" + Guid.NewGuid().ToString("N");
                await ForwardDonationToModAsync(_cfg.subscription_value_usd, username, "sltest_sub", key);
                return;
            }

            Log("Usage: sltest tip 5 name | sltest bits 300 name | sltest sub name");
        }

        // =========================
        // Local WebSocket server
        // =========================
        private static async Task RunWebSocketServerAsync(int port, CancellationToken ct)
        {
            string prefix = "http://127.0.0.1:" + port + "/";

            using (var listener = new HttpListener())
            {
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
        }

        private static async Task HandleClientAsync(HttpListenerContext ctx)
        {
            WebSocketContext wsCtx = null;
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
        // Status packets to mod
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
                local_ws_port = _cfg.local_ws_port,
                clients = Clients.Count,
                streamlabs_connected = _streamlabsConnected,
                streamlabs_last_event_utc = (_streamlabsLastEventUtc == DateTime.MinValue) ? "" : _streamlabsLastEventUtc.ToString("u"),
                streamlabs_last_event_type = _streamlabsLastEventType ?? "",
                streamlabs_last_error = _streamlabsLastError ?? ""
            }, _cfg.log_broadcast_status);
        }

        private static Task SendStatusAsync(WebSocket ws)
        {
            return SendJsonAsync(ws, new
            {
                type = "status",
                bridge = "ok",
                local_ws_port = _cfg.local_ws_port,
                clients = Clients.Count,
                streamlabs_connected = _streamlabsConnected,
                streamlabs_last_event_utc = (_streamlabsLastEventUtc == DateTime.MinValue) ? "" : _streamlabsLastEventUtc.ToString("u"),
                streamlabs_last_event_type = _streamlabsLastEventType ?? "",
                streamlabs_last_error = _streamlabsLastError ?? ""
            });
        }

        // =========================
        // Streamlabs loop
        // =========================
        private static async Task RunStreamlabsLoopAsync(CancellationToken ct)
        {
            if (!_cfg.streamlabs_enabled)
            {
                Log("Streamlabs disabled (streamlabs_enabled=false).");
                return;
            }

            if (string.IsNullOrWhiteSpace(_cfg.streamlabs_socket_token))
            {
                Log("Streamlabs token missing. Paste it into config.json then restart Bridge.");
                return;
            }

            await StartStreamlabsAsync();
            await BroadcastStatusAsync();

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_streamlabs == null)
                    {
                        await StartStreamlabsAsync();
                        await BroadcastStatusAsync();
                    }
                    else if (!_streamlabs.Connected)
                    {
                        _streamlabsConnected = false;
                        await BroadcastStatusAsync();

                        await Task.Delay(5000, ct).ContinueWith(_ => { });
                        if (!ct.IsCancellationRequested)
                        {
                            try { await _streamlabs.ConnectAsync(); }
                            catch (Exception ex) { _streamlabsLastError = ex.Message; }
                        }
                    }
                }
                catch { }

                await Task.Delay(500, ct).ContinueWith(_ => { });
            }
        }

        private static async Task RestartStreamlabsAsync()
        {
            await StopStreamlabsAsync();
            await StartStreamlabsAsync();
            await BroadcastStatusAsync();
        }

        private static async Task StartStreamlabsAsync()
        {
            try
            {
                var uri = new Uri("https://sockets.streamlabs.com");

                var query = new NameValueCollection();
                query["token"] = _cfg.streamlabs_socket_token;

                var options = new SocketIOOptions
                {
                    EIO = EngineIO.V3,
                    Reconnection = false,
                    Query = query
                };

                TryForceWebSocketTransport(options);

                _streamlabs = new SocketIO(uri, options);

                _streamlabs.OnConnected += (sender, e) =>
                {
                    _streamlabsConnected = true;
                    _streamlabsLastError = "";
                    Log("Streamlabs: CONNECTED");
                    _ = BroadcastStatusAsync();
                };

                _streamlabs.OnDisconnected += (sender, reason) =>
                {
                    _streamlabsConnected = false;
                    Log("Streamlabs: DISCONNECTED (" + reason + ")");
                    _ = BroadcastStatusAsync();
                };

                _streamlabs.OnError += (sender, err) =>
                {
                    _streamlabsConnected = false;
                    _streamlabsLastError = err;
                    Log("Streamlabs: ERROR (" + err + ")");
                    _ = BroadcastStatusAsync();
                };

                _streamlabs.On("event", async ctx =>
                {
                    try
                    {
                        string rawJson = ExtractStreamlabsPayload(ctx);
                        if (string.IsNullOrEmpty(rawJson))
                            return;

                        if (_cfg.verbose_streamlabs_logging)
                            Log("Streamlabs 'event' payload: " + rawJson);

                        JObject data;
                        try { data = JObject.Parse(rawJson); }
                        catch { return; }

                        string type = (data["type"] ?? "").ToString();
                        string forField = (data["for"] ?? "").ToString();
                        if (string.IsNullOrEmpty(forField)) forField = "streamlabs";

                        if (type == "alertPlaying" || type == "alertEnded" || type == "alertStart" ||
                            type == "streamlabels" || type == "streamlabels.underlying")
                            return;

                        JArray msgs = data["message"] as JArray;
                        if (msgs == null || msgs.Count == 0)
                            return;

                        _streamlabsLastEventUtc = DateTime.UtcNow;
                        _streamlabsLastEventType = (forField + ":" + type);
                        _streamlabsLastError = "";
                        await BroadcastStatusAsync();

                        if (forField == "streamlabs" && type == "donation")
                        {
                            if (!_cfg.enable_tips) return;

                            foreach (var mTok in msgs)
                            {
                                var m = mTok as JObject;
                                if (m == null) continue;

                                double amountUsd = ParseDouble(m["amount"]);
                                string username = (m["name"] ?? m["from"] ?? "unknown").ToString();
                                string key = BuildDedupeKeyPreferId(m);

                                await ForwardDonationToModAsync(amountUsd, username, "streamlabs_event", key);
                            }
                            return;
                        }

                        if (forField == "twitch_account")
                        {
                            if (type == "bits")
                            {
                                if (!_cfg.enable_bits) return;

                                foreach (var mTok in msgs)
                                {
                                    var m = mTok as JObject;
                                    if (m == null) continue;

                                    double bits = ParseDouble(m["amount"]);
                                    string username = (m["name"] ?? m["from"] ?? "unknown").ToString();

                                    double usd = (_cfg.bits_per_usd <= 0.0) ? 0.0 : (bits / _cfg.bits_per_usd);

                                    string key = BuildDedupeKeyPreferId(m);

                                    await ForwardDonationToModAsync(usd, username, "twitch_bits", key);
                                }
                                return;
                            }

                            if (type == "subscription")
                            {
                                if (!_cfg.enable_subscriptions) return;

                                foreach (var mTok in msgs)
                                {
                                    var m = mTok as JObject;
                                    if (m == null) continue;

                                    string username = (m["name"] ?? m["from"] ?? "unknown").ToString();
                                    string key = BuildDedupeKeyPreferId(m);

                                    await ForwardDonationToModAsync(_cfg.subscription_value_usd, username, "twitch_subscription", key);
                                }
                                return;
                            }

                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("Streamlabs handler error: " + ex.Message);
                    }
                });

                _streamlabs.On("donations", async ctx =>
                {
                    try
                    {
                        if (!_cfg.enable_tips) return;

                        var raw = GetProp(ctx, "RawText") as string;
                        if (string.IsNullOrEmpty(raw)) return;

                        if (_cfg.verbose_streamlabs_logging)
                            Log("Streamlabs 'donations' raw: " + raw);

                        var arr = JArray.Parse(raw);
                        if (arr.Count < 2) return;

                        var list = arr[1] as JArray;
                        if (list == null) return;

                        _streamlabsLastEventUtc = DateTime.UtcNow;
                        _streamlabsLastEventType = "streamlabs:donations";
                        _streamlabsLastError = "";
                        await BroadcastStatusAsync();

                        foreach (var t in list)
                        {
                            var m = t as JObject;
                            if (m == null) continue;

                            double amountUsd = ParseDouble(m["amount"]);
                            string username = (m["name"] ?? m["from"] ?? "unknown").ToString();
                            string key = BuildDedupeKeyPreferId(m);

                            await ForwardDonationToModAsync(amountUsd, username, "streamlabs_donations", key);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("Streamlabs donations handler error: " + ex.Message);
                    }
                });

                Log("Streamlabs: connecting...");
                await _streamlabs.ConnectAsync();
            }
            catch (Exception ex)
            {
                _streamlabsConnected = false;
                _streamlabsLastError = ex.Message;
                Log("Streamlabs start error: " + ex.Message);
            }
        }

        private static async Task StopStreamlabsAsync()
        {
            try
            {
                if (_streamlabs != null)
                {
                    try { if (_streamlabs.Connected) await _streamlabs.DisconnectAsync(); } catch { }
                    try { _streamlabs.Dispose(); } catch { }
                    _streamlabs = null;
                }
            }
            catch { }
        }

        // =========================
        // Donation forwarding + dedupe
        // =========================
        private static async Task ForwardDonationToModAsync(double amount, string username, string source, string dedupeKey)
        {
            if (amount <= 0.0) return;

            if (ShouldSkipAsDuplicate(dedupeKey))
            {
                if (_cfg.verbose_streamlabs_logging)
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

        private static string BuildDedupeKeyPreferId(JObject msg)
        {
            if (msg == null) return null;

            var id = msg["id"]?.ToString();
            if (!string.IsNullOrEmpty(id))
                return "id:" + id;

            var _id = msg["_id"]?.ToString();
            if (!string.IsNullOrEmpty(_id))
                return "_id:" + _id;

            var name = (msg["name"] ?? msg["from"] ?? "unknown")?.ToString();
            var amount = (msg["amount"] ?? "0")?.ToString();
            var text = (msg["message"] ?? "")?.ToString();
            return $"sig:{name}|{amount}|{text}";
        }

        // =========================
        // Payload extraction helpers
        // =========================
        private static string ExtractStreamlabsPayload(IEventContext ctx)
        {
            try
            {
                var rawText = GetProp(ctx, "RawText") as string;
                if (!string.IsNullOrEmpty(rawText))
                {
                    try
                    {
                        var arr = JArray.Parse(rawText);
                        if (arr.Count >= 2)
                            return arr[1].ToString(Formatting.None);
                    }
                    catch
                    {
                        return rawText;
                    }
                }

                var argsObj = GetProp(ctx, "Arguments");
                if (argsObj is System.Collections.IEnumerable enumerable)
                {
                    object first = null;
                    foreach (var item in enumerable) { first = item; break; }

                    if (first != null)
                    {
                        if (first is JToken jt) return jt.ToString(Formatting.None);
                        if (first is JObject jo) return jo.ToString(Formatting.None);
                        if (first is string s && !string.IsNullOrEmpty(s)) return s;

                        try
                        {
                            string serialized = JsonConvert.SerializeObject(first);
                            if (!string.IsNullOrEmpty(serialized)) return serialized;
                        }
                        catch { }
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static object GetProp(object obj, string propName)
        {
            if (obj == null) return null;
            try
            {
                var p = obj.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                return p?.GetValue(obj);
            }
            catch { return null; }
        }

        private static void TryForceWebSocketTransport(SocketIOOptions options)
        {
            try
            {
                var p = options.GetType().GetProperty("Transport", BindingFlags.Public | BindingFlags.Instance);
                if (p == null || !p.CanWrite) return;

                if (p.PropertyType.IsEnum)
                {
                    object enumVal = Enum.Parse(p.PropertyType, "WebSocket", ignoreCase: true);
                    p.SetValue(options, enumVal, null);
                    return;
                }

                if (p.PropertyType == typeof(string))
                {
                    p.SetValue(options, "websocket", null);
                    return;
                }
            }
            catch { }
        }

        private static double ParseDouble(JToken token)
        {
            if (token == null) return 0.0;

            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
                return token.Value<double>();

            double v;
            if (double.TryParse(token.ToString(), out v))
                return v;

            return 0.0;
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
                Console.WriteLine("[BRIDGE] Failed to parse config.json, using defaults.");
                return new Config();
            }
        }

        private static void Log(string msg)
        {
            string line = "[BRIDGE] " + msg;
            Console.WriteLine(line);
            try { _logWriter?.WriteLine(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC' ") + line); } catch { }
        }
    }
}