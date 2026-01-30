using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using UnityMCP.Editor;
using Newtonsoft.Json;

public class ScriptTester : EditorWindow
{
    private string scriptCode = "using UnityEngine;\nusing UnityEditor;\nusing System;\nusing System.Collections.Generic;\nusing System.Linq;\n\npublic class EditorCommand\n{\n    public static object Execute()\n    {\n        return \"Hello from Script Tester!\";\n    }\n}";
    private Vector2 codeScrollPosition;
    private Vector2 resultScrollPosition;
    private Vector2 mainScrollPosition;
    private string resultText = "";
    private bool hasError = false;
    private List<string> logs = new List<string>();
    private float codeEditorHeight = 400f;
    private bool isDraggingSplitter = false;
    
    [MenuItem("UnityMCP/Script Tester")]
    public static void ShowWindow()
    {
        GetWindow<ScriptTester>("Script Tester");
    }
    
    void OnGUI()
    {
        mainScrollPosition = EditorGUILayout.BeginScrollView(mainScrollPosition, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        
        EditorGUILayout.Space(5);
        
        EditorGUILayout.LabelField("Enter C# Script:", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Your script must contain a class named 'EditorCommand' with a static method 'Execute' that returns an object. Code surrounded by `` will be JSON parsed.", MessageType.Info);
        
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        codeScrollPosition = EditorGUILayout.BeginScrollView(codeScrollPosition, GUILayout.Height(codeEditorHeight));
        scriptCode = EditorGUILayout.TextArea(scriptCode, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndVertical();
        
        DrawSplitter(ref codeEditorHeight, 100f, 500f);
        
        EditorGUILayout.Space(10);
        
        GUI.backgroundColor = new Color(0.7f, 0.9f, 0.7f);
        if (GUILayout.Button("Execute Script", GUILayout.Height(30)))
        {
            ExecuteScript();
        }
        GUI.backgroundColor = Color.white;
        
        EditorGUILayout.Space(10);
        
        EditorGUILayout.LabelField("Results:", EditorStyles.boldLabel);
        
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        resultScrollPosition = EditorGUILayout.BeginScrollView(resultScrollPosition, GUILayout.Height(75));
        
        if (hasError)
        {
            EditorGUILayout.HelpBox(resultText, MessageType.Error);
        }
        else if (!string.IsNullOrEmpty(resultText))
        {
            EditorGUILayout.SelectableLabel(resultText, EditorStyles.textField, GUILayout.ExpandHeight(true));
        }
        
        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndVertical();
        
        if (logs.Count > 0)
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Logs:", EditorStyles.boldLabel);
            
            foreach (var log in logs)
            {
                EditorGUILayout.HelpBox(log, MessageType.Info);
            }
        }
        
        EditorGUILayout.EndScrollView();
    }
    
    private void ExecuteScript()
    {
        logs.Clear();
        hasError = false;
        resultText = "Executing script...";
        
        Repaint();
        
        EditorApplication.delayCall += ExecuteAfterRepaint;
    }
    
    private void ExecuteAfterRepaint()
    {
        EditorApplication.delayCall -= ExecuteAfterRepaint;
        
        Application.logMessageReceived += LogHandler;
        
        string processedCode = scriptCode;
        if (scriptCode.StartsWith("`"))
        {
            processedCode = ProcessJsonStringCode(scriptCode);
        }
        
        var result = UnityMCP.Editor.EditorCommandExecutor.CompileAndExecute(processedCode);
        
        if (result != null)
        {
            resultText = "Result: " + result.ToString();
        }
        else
        {
            resultText = "Result: null";
        }
        
        Application.logMessageReceived -= LogHandler;
        
        Repaint();
    }
    
    private string ProcessJsonStringCode(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;
            
        if (input.StartsWith("`"))
            input = input.Trim('`');
        
        string[] lines = input.Split(new[] { '\n', '\r' }, StringSplitOptions.None);
        List<string> sanitizedLines = new List<string>();
        
        for (int i = 0; i < lines.Length; i++)
        {
            string currentLine = lines[i];
            
            while (i < lines.Length - 1 && currentLine.TrimEnd().EndsWith("\\"))
            {
                currentLine = currentLine.TrimEnd().TrimEnd('\\') + lines[i + 1].TrimStart();
                i++;
            }
            
            sanitizedLines.Add(currentLine);
        }
        
        input = string.Join("\n", sanitizedLines);
            
        string unescaped = JsonConvert.DeserializeObject<string>('"' + input + '"');
        return unescaped;
    }
    
    private void LogHandler(string message, string stackTrace, LogType type)
    {
        if (type == LogType.Log)
        {
            logs.Add(message);
        }
        else if (type == LogType.Warning)
        {
            logs.Add($"Warning: {message}");
        }
        else if (type == LogType.Error || type == LogType.Exception)
        {
            logs.Add($"Error: {message}\n{stackTrace}");
        }
    }
    
    private void DrawSplitter(ref float heightToAdjust, float minHeight, float maxHeight)
    {
        EditorGUILayout.Space(2);
        
        Rect splitterRect = EditorGUILayout.GetControlRect(false, 5f);
        EditorGUI.DrawRect(splitterRect, new Color(0.5f, 0.5f, 0.5f, 0.5f));
        
        EditorGUIUtility.AddCursorRect(splitterRect, MouseCursor.ResizeVertical);
        
        Event e = Event.current;
        switch (e.type)
        {
            case EventType.MouseDown:
                if (splitterRect.Contains(e.mousePosition))
                {
                    isDraggingSplitter = true;
                    e.Use();
                }
                break;
                
            case EventType.MouseDrag:
                if (isDraggingSplitter)
                {
                    heightToAdjust += e.delta.y;
                    heightToAdjust = Mathf.Clamp(heightToAdjust, minHeight, maxHeight);
                    e.Use();
                    Repaint();
                }
                break;
                
            case EventType.MouseUp:
                if (isDraggingSplitter)
                {
                    isDraggingSplitter = false;
                    e.Use();
                }
                break;
        }
        
        EditorGUILayout.Space(2);
    }
}