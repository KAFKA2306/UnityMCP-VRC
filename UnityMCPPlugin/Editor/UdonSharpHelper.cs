using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System.IO;
using System;
using UnityEditor.Compilation;

namespace UnityMCP.VRChatUtils
{
    public static class UdonSharpHelper
    {
        public static string CreateAsset(string scriptPath)
        {
            if (string.IsNullOrEmpty(scriptPath) || !scriptPath.EndsWith(".cs"))
            {
                ThrowAndLog("Invalid script path: " + scriptPath);
            }

            string scriptGuid = AssetDatabase.AssetPathToGUID(scriptPath);
            if (string.IsNullOrEmpty(scriptGuid))
            {
                ThrowAndLog("Could not find GUID for script at path: " + scriptPath);
            }

            string udonSharpProgramAssetGuid = GetUdonSharpProgramAssetGuid();
            if (string.IsNullOrEmpty(udonSharpProgramAssetGuid))
            {
                ThrowAndLog("UdonSharpProgramAsset GUID not found. Please ensure UdonSharp is installed correctly.");
            }

            string scriptName = Path.GetFileNameWithoutExtension(scriptPath);
            string assetName = scriptName;
            string scriptDirectory = Path.GetDirectoryName(scriptPath);
            string assetPath = Path.Combine(scriptDirectory, assetName + ".asset");

            var assetData = $@"%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!114 &11400000
MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: 0}}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {{fileID: 11500000, guid: {udonSharpProgramAssetGuid}, type: 3}}
  m_Name: {assetName}
  m_EditorClassIdentifier: 
  sourceCsScript: {{fileID: 11500000, guid: {scriptGuid}, type: 3}}
  behaviourSyncMode: 0
  behaviourIDHeapVarName: 
  variableNames: []
  variableValues: []";

            File.WriteAllText(assetPath, assetData);
            Debug.Log($"Created UdonSharp asset at: {assetPath}");

            AssetDatabase.Refresh();

            CompilationPipeline.RequestScriptCompilation();

            return assetPath;
        }

        private static string GetUdonSharpProgramAssetGuid()
        {
            string[] guids = AssetDatabase.FindAssets("UdonSharpProgramAsset t:MonoScript");
            foreach (string guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.Contains("UdonSharpProgramAsset.cs"))
                {
                    return guid;
                }
            }

            return string.Empty;
        }

        private static void ThrowAndLog(string message)
        {
            Debug.LogError(message);
            throw new Exception(message);
        }
    }
}