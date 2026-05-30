using UnityEngine;
using UnityEditor;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace UnityMCP.Editor
{
    // Unity HOSTS the WebSocket server; each MCP server process connects to it as a client.
    //
    // Previously this was reversed (Unity dialed out to a server each Claude session launched),
    // which made the MCP servers fight over a fixed port 8080 - only the first bound it and the
    // rest lingered as zombies that could never reach Unity. Inverting it lets any number of MCP
    // servers connect to the one Editor at once. Each request carries an "id"; responses echo it
    // back on the originating socket so a client matches a reply to its own in-flight call.
    //
    // This class owns the server lifecycle (bind/accept/teardown), the connected-client list,
    // message dispatch, and log broadcast. The WebSocket wire protocol itself lives in
    // ClientConnection (handshake + RFC 6455 framing).
    [InitializeOnLoad]
    public class UnityMCPConnection
    {
        private const int Port = 8080;

        private static TcpListener listener;
        private static bool isListening;
        private static CancellationTokenSource serverCts;
        private static readonly List<ClientConnection> clients = new List<ClientConnection>();

        private static string lastErrorMessage = "";
        private static readonly Queue<LogEntry> logBuffer = new Queue<LogEntry>();
        private static readonly int maxLogBufferSize = 1000;
        private static bool isLoggingEnabled = true;
        private static readonly EditorStateReporter editorStateReporter = new EditorStateReporter();

        // Diagnostics surfaced in the debug window.
        private static DateTime serverStartedUtc;
        private static string lastRequestType;
        private static DateTime lastRequestUtc;
        private static int totalRequests;

        // Public properties for the debug window.
        // IsListening is the authoritative signal: the server owns the port and is accepting
        // connections. False means the bind failed (e.g. another process holds the port) - see
        // LastErrorMessage. IsConnected/ConnectedClientCount report how many clients are attached.
        public static bool IsListening => isListening;
        public static bool IsConnected { get { lock (clients) { return clients.Count > 0; } } }
        public static int ConnectedClientCount { get { lock (clients) { return clients.Count; } } }
        public static Uri ServerUri => new Uri($"ws://localhost:{Port}/");
        public static string LastErrorMessage => lastErrorMessage;
        public static DateTime ServerStartedUtc => serverStartedUtc;
        public static string LastRequestType => lastRequestType;
        public static DateTime LastRequestUtc => lastRequestUtc;
        public static int TotalRequestCount => totalRequests;
        public static int BufferedLogCount { get { lock (logBuffer) { return logBuffer.Count; } } }

        // Snapshot of currently connected clients, for the debug window.
        public readonly struct ClientInfo
        {
            public readonly string Endpoint;
            public readonly DateTime ConnectedAtUtc;
            public ClientInfo(string endpoint, DateTime connectedAtUtc)
            {
                Endpoint = endpoint;
                ConnectedAtUtc = connectedAtUtc;
            }
        }

        public static ClientInfo[] GetConnectedClients()
        {
            lock (clients)
            {
                var arr = new ClientInfo[clients.Count];
                for (int i = 0; i < clients.Count; i++)
                {
                    arr[i] = new ClientInfo(clients[i].RemoteEndPoint, clients[i].ConnectedAtUtc);
                }
                return arr;
            }
        }
        public static bool IsLoggingEnabled
        {
            get => isLoggingEnabled;
            set
            {
                isLoggingEnabled = value;
                if (value)
                {
                    Application.logMessageReceived += HandleLogMessage;
                }
                else
                {
                    Application.logMessageReceived -= HandleLogMessage;
                }
            }
        }

        private class LogEntry
        {
            public string Message { get; set; }
            public string StackTrace { get; set; }
            public LogType Type { get; set; }
            public DateTime Timestamp { get; set; }
        }

        // Manual restart from the debug window (e.g. after a port-bind failure is resolved).
        public static void RetryConnection()
        {
            Debug.Log("[UnityMCP] Restarting MCP server...");
            StopServer();
            StartServer();
        }

        // Constructor called on editor startup (and again after every domain reload).
        static UnityMCPConnection()
        {
            Application.logMessageReceived += HandleLogMessage;
            isLoggingEnabled = true;

            Debug.Log("[UnityMCP] Plugin initialized");
            EditorApplication.delayCall += () =>
            {
                Debug.Log("[UnityMCP] Starting MCP WebSocket server");
                StartServer();
            };
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        // A domain reload (recompiles, including execute_editor_command edits) wipes all managed
        // state here - the listener, every client socket, and EditorUtilities' main-thread queue.
        // Tear the server down synchronously now so connected MCP clients see the close
        // immediately, fail their in-flight requests with a "retry" instead of waiting out a
        // timeout, and reconnect once the server restarts after the reload (this static
        // constructor runs again in the new domain).
        private static void OnBeforeAssemblyReload()
        {
            // Announce the reload BEFORE dropping sockets so clients can distinguish a clean reload
            // (queued requests were dropped before they ran - safe to retry) from an arbitrary
            // disconnect (an in-flight command may have applied). Best-effort and synchronous: the
            // domain is unloading, so async sends might not flush.
            NotifyClientsReloading();
            StopServer();
        }

        // Synchronously tell every connected client we're about to reload. A queued request can't
        // have run yet (beforeAssemblyReload is on the main thread, so nothing is mid-execution),
        // so anything still pending on a client when the socket then closes was dropped before
        // running - the client uses this notice to say "safe to retry" instead of "may have applied".
        private static void NotifyClientsReloading()
        {
            List<ClientConnection> snapshot;
            lock (clients)
            {
                if (clients.Count == 0) return;
                snapshot = new List<ClientConnection>(clients);
            }

            var message = JsonConvert.SerializeObject(new { type = "reloading" });
            foreach (var c in snapshot)
            {
                try { c.TrySendTextBlocking(message); } catch { }
            }
        }

        private static void StartServer()
        {
            if (isListening) return;

            try
            {
                serverCts = new CancellationTokenSource();
                listener = new TcpListener(IPAddress.IPv6Any, Port);
                // Accept both IPv4 (127.0.0.1) and IPv6 (::1) clients. If the runtime refuses
                // dual-mode we still listen on IPv6, which is what "localhost" resolves to here.
                try { listener.Server.DualMode = true; } catch { }
                listener.Start();
                isListening = true;
                serverStartedUtc = DateTime.UtcNow;
                lastErrorMessage = "";
                Debug.Log($"[UnityMCP] WebSocket server listening on ws://localhost:{Port}/");
                _ = AcceptLoop(serverCts.Token);
            }
            catch (Exception e)
            {
                isListening = false;
                lastErrorMessage = $"[UnityMCP] Failed to start server on port {Port}: {e.Message}";
                Debug.LogError(lastErrorMessage);
                listener = null;
            }
        }

        private static void StopServer()
        {
            isListening = false;
            try { serverCts?.Cancel(); } catch { }

            // Close outstanding client sockets so their receive loops unwind promptly.
            lock (clients)
            {
                foreach (var c in clients)
                {
                    try { c.Close(); } catch { }
                }
                clients.Clear();
            }

            try { listener?.Stop(); } catch { }
            listener = null;
        }

        private static async Task AcceptLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && isListening)
            {
                TcpClient tcp;
                try
                {
                    tcp = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    break; // listener stopped/disposed - exit quietly
                }

                if (token.IsCancellationRequested)
                {
                    try { tcp.Close(); } catch { }
                    break;
                }

                _ = HandleClient(tcp, token);
            }
        }

        private static async Task HandleClient(TcpClient tcp, CancellationToken token)
        {
            ClientConnection client = null;
            try
            {
                client = await ClientConnection.AcceptAsync(tcp, token).ConfigureAwait(false);
                if (client == null)
                {
                    Debug.LogWarning("[UnityMCP] WebSocket handshake failed (not a valid upgrade request)");
                    return;
                }

                lock (clients) clients.Add(client);
                Debug.Log($"[UnityMCP] MCP client connected ({ConnectedClientCount} total)");

                while (!token.IsCancellationRequested)
                {
                    string message;
                    try
                    {
                        message = await client.ReceiveMessageAsync(token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        break; // connection dropped
                    }

                    if (message == null) break; // closed
                    await HandleMessage(client, message, token).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[UnityMCP] Client error: {e.Message}");
            }
            finally
            {
                if (client != null)
                {
                    lock (clients) clients.Remove(client);
                    client.Close();
                    Debug.Log($"[UnityMCP] MCP client disconnected ({ConnectedClientCount} remaining)");
                }
                else
                {
                    try { tcp.Close(); } catch { }
                }
            }
        }

        private static async Task HandleMessage(ClientConnection client, string message, CancellationToken token)
        {
            string id = null;
            string type = null;
            try
            {
                var data = JsonConvert.DeserializeObject<Dictionary<string, object>>(message);
                type = data.ContainsKey("type") ? data["type"]?.ToString() : null;
                id = data.ContainsKey("id") ? data["id"]?.ToString() : null;
                var payload = data.ContainsKey("data") && data["data"] != null ? data["data"].ToString() : "{}";

                if (type == "executeEditorCommand" || type == "getEditorState")
                {
                    lastRequestType = type;
                    lastRequestUtc = DateTime.UtcNow;
                    Interlocked.Increment(ref totalRequests);
                }

                switch (type)
                {
                    case "executeEditorCommand":
                    {
                        object resultData;
                        try
                        {
                            resultData = await EditorCommandExecutor.ExecuteAndGetResult(payload).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            // e.g. RunOnMainThread timed out (Editor unfocused). Return a failed
                            // result so the client reports it instead of waiting out its timeout.
                            resultData = new
                            {
                                result = (object)null,
                                logs = new List<string>(),
                                errors = new List<string> { e.Message },
                                warnings = new List<string>(),
                                executionSuccess = false,
                                errorDetails = new { message = e.Message, stackTrace = "", type = e.GetType().Name }
                            };
                        }
                        await SendResponse(client, "commandResult", id, resultData, token).ConfigureAwait(false);
                        break;
                    }
                    case "getEditorState":
                    {
                        var stateData = await editorStateReporter.GetEditorStateData().ConfigureAwait(false);
                        await SendResponse(client, "editorState", id, stateData, token).ConfigureAwait(false);
                        break;
                    }
                    default:
                        Debug.LogWarning($"[UnityMCP] Unknown message type: {type}");
                        break;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[UnityMCP] Error handling message: {e.Message}");
            }
        }

        private static Task SendResponse(ClientConnection client, string type, string id, object data, CancellationToken token)
        {
            var message = JsonConvert.SerializeObject(new { type, id, data });
            return client.SendTextAsync(message, token);
        }

        private static void HandleLogMessage(string message, string stackTrace, LogType type)
        {
            if (!isLoggingEnabled) return;

            var logEntry = new LogEntry
            {
                Message = message,
                StackTrace = stackTrace,
                Type = type,
                Timestamp = DateTime.UtcNow
            };

            lock (logBuffer)
            {
                logBuffer.Enqueue(logEntry);
                while (logBuffer.Count > maxLogBufferSize)
                {
                    logBuffer.Dequeue();
                }
            }

            BroadcastLog(logEntry);
        }

        // Push a log line to every connected client (each MCP server keeps its own buffer).
        private static void BroadcastLog(LogEntry logEntry)
        {
            List<ClientConnection> snapshot;
            lock (clients)
            {
                if (clients.Count == 0) return;
                snapshot = new List<ClientConnection>(clients);
            }

            var message = JsonConvert.SerializeObject(new
            {
                type = "log",
                data = new
                {
                    message = logEntry.Message,
                    stackTrace = logEntry.StackTrace,
                    logType = logEntry.Type.ToString(),
                    timestamp = logEntry.Timestamp
                }
            });

            foreach (var client in snapshot)
            {
                _ = SafeSend(client, message);
            }
        }

        private static async Task SafeSend(ClientConnection client, string message)
        {
            try
            {
                await client.SendTextAsync(message, serverCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Client is likely gone; its receive loop will remove it.
            }
        }
    }
}
