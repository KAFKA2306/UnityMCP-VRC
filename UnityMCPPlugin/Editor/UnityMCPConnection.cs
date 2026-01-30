using UnityEngine;
using UnityEditor;
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Microsoft.CSharp;
using System.CodeDom.Compiler;

namespace UnityMCP.Editor
{
    [InitializeOnLoad]
    public class UnityMCPConnection
    {
        private static ClientWebSocket webSocket;
        private static bool isConnected = false;
        private static readonly Uri serverUri = new Uri("ws://localhost:8080");
        private static readonly Queue<LogEntry> logBuffer = new Queue<LogEntry>();
        private static readonly int maxLogBufferSize = 1000;
        private static bool isLoggingEnabled = true;
        private static EditorStateReporter editorStateReporter;

        public static bool IsConnected => isConnected;
        public static Uri ServerUri => serverUri;
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
        private static readonly CancellationTokenSource cts = new CancellationTokenSource();

        static UnityMCPConnection()
        {
            Application.logMessageReceived += HandleLogMessage;
            isLoggingEnabled = true;

            Debug.Log("[UnityMCP] Plugin initialized");
            EditorApplication.delayCall += () =>
            {
                Debug.Log("[UnityMCP] Starting initial connection");
                ConnectToServer();
            };
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

            if (isConnected && webSocket?.State == WebSocketState.Open)
            {
                SendLogToServer(logEntry);
            }
        }

        private static async void SendLogToServer(LogEntry logEntry)
        {
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

            var buffer = Encoding.UTF8.GetBytes(message);
            await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cts.Token);
        }

        public static string[] GetRecentLogs(LogType[] types = null, int count = 100)
        {
            lock (logBuffer)
            {
                var logs = logBuffer.ToArray()
                    .Where(log => types == null || types.Contains(log.Type))
                    .TakeLast(count)
                    .Select(log => $"[{log.Timestamp:yyyy-MM-dd HH:mm:ss}] [{log.Type}] {log.Message}")
                    .ToArray();
                return logs;
            }
        }

        private static async void ConnectToServer()
        {
            if (webSocket != null &&
                (webSocket.State == WebSocketState.Connecting ||
                 webSocket.State == WebSocketState.Open))
            {
                return;
            }

            Debug.Log($"[UnityMCP] Attempting to connect to MCP Server at {serverUri}");

            webSocket = new ClientWebSocket();
            webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(60);

            await webSocket.ConnectAsync(serverUri, cts.Token);
            isConnected = true;
            Debug.Log("[UnityMCP] Successfully connected to MCP Server");
            StartReceiving();
            
            editorStateReporter = new EditorStateReporter();
        }

        private static async void StartReceiving()
        {
            var buffer = new byte[1024 * 4];
            var messageBuffer = new List<byte>();
            
            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    messageBuffer.AddRange(new ArraySegment<byte>(buffer, 0, result.Count));
                    
                    if (result.EndOfMessage)
                    {
                        var message = Encoding.UTF8.GetString(messageBuffer.ToArray());
                        await HandleMessage(message);
                        messageBuffer.Clear();
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cts.Token);
                    isConnected = false;
                    Debug.Log("[UnityMCP] WebSocket connection closed normally");
                    break;
                }
            }
        }

        private static async Task HandleMessage(string message)
        {
            var data = JsonConvert.DeserializeObject<Dictionary<string, object>>(message);
            switch (data["type"].ToString())
            {
                case "executeEditorCommand":
                    await EditorCommandExecutor.ExecuteEditorCommand(webSocket, cts.Token, data["data"].ToString());
                    break;
                case "getEditorState":
                    await editorStateReporter.SendEditorState(webSocket, cts.Token);
                    break;
            }
        }
    }
}