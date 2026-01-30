using UnityEngine;
using UnityEditor;
using System;

namespace UnityMCP.Editor
{
    public class UnityMCPWindow : EditorWindow
    {
        private bool previousConnectionState;

        [MenuItem("UnityMCP/Debug Window", false, 1)]
        public static void ShowWindow()
        {
            GetWindow<UnityMCPWindow>("UnityMCP Debug");
        }

        void OnEnable()
        {
            previousConnectionState = UnityMCPConnection.IsConnected;
            
            EditorApplication.update += CheckForChanges;
        }

        void OnDisable()
        {
            EditorApplication.update -= CheckForChanges;
        }

        void CheckForChanges()
        {
            bool connectionChanged = previousConnectionState != UnityMCPConnection.IsConnected;
            
            if (connectionChanged)
            {
                previousConnectionState = UnityMCPConnection.IsConnected;
                
                Repaint();
            }
        }

        void OnGUI()
        {
            EditorGUILayout.Space(10);

            GUILayout.Label("UnityMCP Debug", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Connection Status:", GUILayout.Width(120));
            GUI.color = UnityMCPConnection.IsConnected ? Color.green : Color.red;
            EditorGUILayout.LabelField(UnityMCPConnection.IsConnected ? "Connected" : "Disconnected", EditorStyles.boldLabel);
            GUI.color = Color.white;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            EditorGUILayout.LabelField("MCP Server URI:", GUILayout.Width(120));
            EditorGUILayout.SelectableLabel(UnityMCPConnection.ServerUri.ToString(), EditorStyles.textField, GUILayout.Height(20));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(10);
        }
    }
}