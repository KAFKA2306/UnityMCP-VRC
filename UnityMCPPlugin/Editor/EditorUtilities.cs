using UnityEngine;
using UnityEditor;
using System;

namespace UnityMCP.Editor
{
    public static class EditorUtilities
    {
        public static bool WaitForUnityCompilation()
        {
            if (!EditorApplication.isCompiling)
                return true;

            Debug.Log("[UnityMCP] Waiting for Unity to finish compilation...");

            bool complete = false;

            EditorApplication.CallbackFunction waiter = null;
            waiter = () =>
            {
                if (!EditorApplication.isCompiling)
                {
                    EditorApplication.update -= waiter;
                    complete = true;
                    Debug.Log("[UnityMCP] Unity compilation completed");
                }
            };

            EditorApplication.update += waiter;

            while (!complete)
            {
                System.Threading.Thread.Sleep(100);
                if (EditorWindow.focusedWindow != null)
                {
                    EditorWindow.focusedWindow.Repaint();
                }
            }

            System.Threading.Thread.Sleep(500);

            return complete;
        }
    }
}