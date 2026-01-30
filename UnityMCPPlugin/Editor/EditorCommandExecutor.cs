using UnityEngine;
using UnityEditor;
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Microsoft.CSharp;
using System.CodeDom.Compiler;
using System.Threading.Tasks;

namespace UnityMCP.Editor
{
    public class EditorCommandExecutor
    {
        public class EditorCommandData
        {
            public string code { get; set; }
        }

        public static async Task ExecuteEditorCommand(ClientWebSocket webSocket, CancellationToken cancellationToken, string commandData)
        {
            var logs = new List<string>();
            var errors = new List<string>();
            var warnings = new List<string>();

            Application.logMessageReceived += LogHandler;

            var commandObj = JsonConvert.DeserializeObject<EditorCommandData>(commandData);
            var code = commandObj.code;

            Debug.Log("[UnityMCP] Executing code...");
            
            var result = CompileAndExecute(code);

            Debug.Log("[UnityMCP] Code executed");

            var resultMessage = JsonConvert.SerializeObject(new
            {
                type = "commandResult",
                data = new
                {
                    result = result,
                    logs = logs,
                    errors = errors,
                    warnings = warnings,
                    executionSuccess = true
                }
            });

            var buffer = Encoding.UTF8.GetBytes(resultMessage);
            await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cancellationToken);
            
            Application.logMessageReceived -= LogHandler;

            void LogHandler(string message, string stackTrace, LogType type)
            {
                switch (type)
                {
                    case LogType.Log:
                        logs.Add(message);
                        break;
                    case LogType.Warning:
                        warnings.Add(message);
                        break;
                    case LogType.Error:
                    case LogType.Exception:
                        var firstStackLine = stackTrace?.Split('\n')?.FirstOrDefault() ?? "";
                        errors.Add($"{message}\n{firstStackLine}");
                        break;
                }
            }
        }


        public static object CompileAndExecute(string code)
        {
            EditorUtilities.WaitForUnityCompilation();

            var options = new System.CodeDom.Compiler.CompilerParameters
            {
                GenerateInMemory = true,
                CompilerOptions = "/nostdlib+ /noconfig"
            };

            HashSet<string> addedAssemblies = new HashSet<string>();

            void AddAssemblyReference(string assemblyPath)
            {
                if (!string.IsNullOrEmpty(assemblyPath) && !addedAssemblies.Contains(assemblyPath))
                {
                    options.ReferencedAssemblies.Add(assemblyPath);
                    addedAssemblies.Add(assemblyPath);
                }
            }

            void AddAssemblyByName(string name)
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == name);
                if (assembly != null)
                {
                    AddAssemblyReference(assembly.Location);
                }
            }

            options.CoreAssemblyFileName = typeof(object).Assembly.Location;

            AddAssemblyReference(typeof(UnityEngine.Object).Assembly.Location);
            AddAssemblyReference(typeof(UnityEditor.Editor).Assembly.Location);

            AddAssemblyReference(typeof(System.Linq.Enumerable).Assembly.Location);
            AddAssemblyReference(typeof(object).Assembly.Location);

            AddAssemblyReference(typeof(UnityMCP.Editor.EditorCommandExecutor).Assembly.Location);

            var netstandardAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "netstandard");
            if (netstandardAssembly != null)
            {
                AddAssemblyReference(netstandardAssembly.Location);
            }

            var commonModules = new[] {
                "UnityEngine.AnimationModule",
                "UnityEngine.CoreModule",
                "UnityEngine.IMGUIModule",
                "UnityEngine.PhysicsModule",
                "UnityEngine.TerrainModule",
                "UnityEngine.TextRenderingModule",
                "UnityEngine.UIModule",
                "Unity.TextMeshPro",
                "Unity.TextMeshPro.Editor"
            };

            foreach (var moduleName in commonModules)
            {
                AddAssemblyByName(moduleName);
            }

            var vrchatAssemblies = new[] {
                "VRC.Udon",
                "VRC.Udon.Common",
                "VRC.Udon.Editor",
                "VRC.Udon.Serialization.OdinSerializer",
                "VRC.Udon.VM",
                "VRC.Udon.Wrapper",
                "UdonSharp.Editor",
                "UdonSharp.Runtime",
                "VRCSDK3",
                "VRCSDKBase",
            };

            foreach (var assemblyName in vrchatAssemblies)
            {
                AddAssemblyByName(assemblyName);
            }

            using (var provider = new Microsoft.CSharp.CSharpCodeProvider())
            {
                var results = provider.CompileAssemblyFromSource(options, code);
                if (results.Errors.HasErrors)
                {
                    foreach (CompilerError error in results.Errors)
                    {
                        Debug.LogError($"Error {error.ErrorNumber}: {error.ErrorText}, Line {error.Line}");
                    }
                    var errors = string.Join("\n", results.Errors.Cast<CompilerError>().Select(e => e.ErrorText));
                    throw new Exception($"Compilation failed:\n{errors}");
                }

                var assembly = results.CompiledAssembly;
                var type = assembly.GetType("EditorCommand");
                var method = type.GetMethod("Execute");
                return method.Invoke(null, null);
            }
        }
    }
}