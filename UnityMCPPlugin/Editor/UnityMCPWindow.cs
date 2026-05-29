using UnityEngine;
using UnityEditor;
using System;

namespace UnityMCP.Editor
{
    public class UnityMCPWindow : EditorWindow
    {
        // State tracking for efficient repainting
        private bool previousListeningState;
        private bool previousConnectionState;
        private string previousErrorMessage;

        [MenuItem("UnityMCP/Debug Window", false, 1)]
        public static void ShowWindow()
        {
            GetWindow<UnityMCPWindow>("UnityMCP Debug");
        }

        void OnEnable()
        {
            // Initialize state tracking
            previousListeningState = UnityMCPConnection.IsListening;
            previousConnectionState = UnityMCPConnection.IsConnected;
            previousErrorMessage = UnityMCPConnection.LastErrorMessage;
            
            // Register for updates
            EditorApplication.update += CheckForChanges;
        }

        void OnDisable()
        {
            // Clean up
            EditorApplication.update -= CheckForChanges;
        }

        void CheckForChanges()
        {
            // Only repaint if something we're displaying has changed
            bool listeningChanged = previousListeningState != UnityMCPConnection.IsListening;
            bool connectionChanged = previousConnectionState != UnityMCPConnection.IsConnected;
            bool errorChanged = previousErrorMessage != UnityMCPConnection.LastErrorMessage;

            if (listeningChanged || connectionChanged || errorChanged)
            {
                // Update cached values
                previousListeningState = UnityMCPConnection.IsListening;
                previousConnectionState = UnityMCPConnection.IsConnected;
                previousErrorMessage = UnityMCPConnection.LastErrorMessage;

                Repaint();
            }
        }

        void OnGUI()
        {
            try
            {
                EditorGUILayout.Space(10);

                GUILayout.Label("UnityMCP Debug", EditorStyles.boldLabel);
                EditorGUILayout.Space(5);

                // Server (port) status - the authoritative signal: did the plugin actually bind
                // the port and is it accepting connections? Distinct from whether any client is on.
                // If this is red, another process is likely holding the port (see Last Error).
                bool listening = UnityMCPConnection.IsListening;
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                EditorGUILayout.LabelField("Server:", GUILayout.Width(120));
                GUI.color = listening ? Color.green : Color.red;
                EditorGUILayout.LabelField(
                    listening ? "Listening" : "NOT listening (port unavailable?)",
                    EditorStyles.boldLabel);
                GUI.color = Color.white;
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(5);

                // How many MCP clients (Claude sessions) are currently connected. With the server
                // healthy, zero clients is normal (idle), not an error - so this isn't shown red.
                int clientCount = UnityMCPConnection.ConnectedClientCount;
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                EditorGUILayout.LabelField("MCP Clients:", GUILayout.Width(120));
                GUI.color = clientCount > 0 ? Color.green : Color.gray;
                EditorGUILayout.LabelField(
                    clientCount > 0 ? $"Connected ({clientCount})" : "None connected",
                    EditorStyles.boldLabel);
                GUI.color = Color.white;
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(5);

                // Server address (selectable, for copy-paste)
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                EditorGUILayout.LabelField("Address:", GUILayout.Width(120));
                EditorGUILayout.SelectableLabel(UnityMCPConnection.ServerUri.ToString(), EditorStyles.textField, GUILayout.Height(20));
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(10);

                // Restart the WebSocket server (e.g. if the port failed to bind)
                if (GUILayout.Button("Restart Server", GUILayout.Height(30)))
                {
                    UnityMCPConnection.RetryConnection();
                }

                EditorGUILayout.Space(10);

                // Last error message if any
                if (!string.IsNullOrEmpty(UnityMCPConnection.LastErrorMessage))
                {
                    EditorGUILayout.LabelField("Last Error:", EditorStyles.boldLabel);
                    EditorGUILayout.HelpBox(UnityMCPConnection.LastErrorMessage, MessageType.Error);
                }
            }
            catch (Exception e)
            {
                EditorGUILayout.HelpBox($"Error in debug window: {e.Message}", MessageType.Error);
            }
        }

        // Remove the old Update method as we're using EditorApplication.update instead
    }
}