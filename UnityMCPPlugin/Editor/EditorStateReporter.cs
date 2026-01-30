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

namespace UnityMCP.Editor
{
    public class EditorStateReporter
    {
        public async Task SendEditorState(ClientWebSocket webSocket, CancellationToken cancellationToken)
        {
            if (webSocket?.State == WebSocketState.Open)
            {
                var state = GetEditorState();
                var message = JsonConvert.SerializeObject(new
                {
                    type = "editorState",
                    data = state
                });
                var buffer = Encoding.UTF8.GetBytes(message);
                await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cancellationToken);
                Debug.Log("[UnityMCP] Sent editor state upon request");
            }
            else
            {
                Debug.LogWarning("[UnityMCP] Cannot send editor state - connection closed");
            }
        }

        public object GetEditorState()
        {
            EditorUtilities.WaitForUnityCompilation();

            var activeGameObjects = new List<string>();
            var selectedObjects = new List<string>();

            var foundObjects = GameObject.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
            if (foundObjects != null)
            {
                foreach (var obj in foundObjects)
                {
                    if (obj != null && !string.IsNullOrEmpty(obj.name))
                    {
                        activeGameObjects.Add(obj.name);
                    }
                }
            }

            var selection = Selection.gameObjects;
            if (selection != null)
            {
                foreach (var obj in selection)
                {
                    if (obj != null && !string.IsNullOrEmpty(obj.name))
                    {
                        selectedObjects.Add(obj.name);
                    }
                }
            }

            var currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var sceneHierarchy = currentScene.IsValid() ? GetSceneHierarchy() : new List<object>();

            var projectStructure = new
            {
                scenes = GetSceneNames() ?? new string[0],
                assets = GetAssetPaths() ?? new string[0]
            };

            return new
            {
                activeGameObjects,
                selectedObjects,
                playModeState = EditorApplication.isPlaying ? "Playing" : "Stopped",
                sceneHierarchy,
                projectStructure
            };
        }

        private object GetSceneHierarchy()
        {
            var roots = new List<object>();
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();

            if (scene.IsValid())
            {
                var rootObjects = scene.GetRootGameObjects();
                if (rootObjects != null)
                {
                    foreach (var root in rootObjects)
                    {
                        if (root != null)
                        {
                            roots.Add(GetGameObjectHierarchy(root));
                        }
                    }
                }
            }

            return roots;
        }

        private object GetGameObjectHierarchy(GameObject obj)
        {
            if (obj == null) return null;

            var children = new List<object>();
            var transform = obj.transform;

            if (transform != null)
            {
                for (int i = 0; i < transform.childCount; i++)
                {
                    var childTransform = transform.GetChild(i);
                    if (childTransform != null && childTransform.gameObject != null)
                    {
                        var childHierarchy = GetGameObjectHierarchy(childTransform.gameObject);
                        if (childHierarchy != null)
                        {
                            children.Add(childHierarchy);
                        }
                    }
                }
            }

            return new
            {
                name = obj.name ?? "Unnamed",
                components = GetComponentNames(obj),
                children = children
            };
        }

        private string[] GetComponentNames(GameObject obj)
        {
            if (obj == null) return new string[0];

            var components = obj.GetComponents<Component>();
            if (components == null) return new string[0];

            var validComponents = new List<string>();
            foreach (var component in components)
            {
                if (component != null)
                {
                    validComponents.Add(component.GetType().Name);
                }
            }

            return validComponents.ToArray();
        }

        private static string[] GetSceneNames()
        {
            var scenes = new List<string>();
            foreach (var scene in EditorBuildSettings.scenes)
            {
                scenes.Add(scene.path);
            }
            return scenes.ToArray();
        }

        private static string[] GetAssetPaths()
        {
            var guids = AssetDatabase.FindAssets("", new[] { "Assets/" });
            var paths = new string[guids.Length];
            for (int i = 0; i < guids.Length; i++)
            {
                paths[i] = AssetDatabase.GUIDToAssetPath(guids[i]);
            }
            return paths;
        }
    }
}