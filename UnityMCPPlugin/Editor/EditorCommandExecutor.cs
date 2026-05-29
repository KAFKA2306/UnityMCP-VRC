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

            try
            {
                var commandObj = JsonConvert.DeserializeObject<EditorCommandData>(commandData);
                var code = commandObj.code;

                Debug.Log($"[UnityMCP] Executing code...");
                // Execute the code directly in the Editor context
                try
                {
                    // Execute the provided code
                    var result = CompileAndExecute(code);

                    Debug.Log($"[UnityMCP] Code executed");

                    // Send back detailed execution results
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
                }
                catch (Exception e)
                {
                    throw new Exception($"Failed to execute command: {e.Message}", e);
                }
            }
            catch (Exception e)
            {
                var firstStackLine = e.StackTrace?.Split('\n')?.FirstOrDefault() ?? "";

                var error = $"[UnityMCP] Failed to execute editor command: {e.Message}\n{firstStackLine}";
                Debug.LogError(error);

                // Send back error information
                var errorMessage = JsonConvert.SerializeObject(new
                {
                    type = "commandResult",
                    data = new
                    {
                        result = (object)null,
                        logs = logs,
                        errors = new List<string>(errors) { error },
                        warnings = warnings,
                        executionSuccess = false,
                        errorDetails = new
                        {
                            message = e.Message,
                            stackTrace = firstStackLine,
                            type = e.GetType().Name
                        }
                    }
                });
                var buffer = Encoding.UTF8.GetBytes(errorMessage);
                await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cancellationToken);
            }
            finally
            {
                Application.logMessageReceived -= LogHandler;
            }

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
            // Wait for any ongoing Unity compilation to finish first
            EditorUtilities.WaitForUnityCompilation();

            // Use Mono's built-in compiler
            var options = new System.CodeDom.Compiler.CompilerParameters
            {
                GenerateInMemory = true,
                // Fixes error: The predefined type 'xxx' is defined multiple times. Using definition from 'mscorlib.dll'
                CompilerOptions = "/nostdlib+ /noconfig"
            };

            // Track added assemblies to avoid duplicates
            HashSet<string> addedAssemblies = new HashSet<string>();

            // Helper method to safely add assembly references
            void AddAssemblyReference(string assemblyPath)
            {
                if (!string.IsNullOrEmpty(assemblyPath) && !addedAssemblies.Contains(assemblyPath))
                {
                    options.ReferencedAssemblies.Add(assemblyPath);
                    addedAssemblies.Add(assemblyPath);
                }
            }

            try
            {
                options.CoreAssemblyFileName = typeof(object).Assembly.Location;

                // Add engine/editor core references
                AddAssemblyReference(typeof(UnityEngine.Object).Assembly.Location);
                AddAssemblyReference(typeof(UnityEditor.Editor).Assembly.Location);

                AddAssemblyReference(typeof(System.Linq.Enumerable).Assembly.Location); // Add System.Core for LINQ
                AddAssemblyReference(typeof(object).Assembly.Location); // Add mscorlib

                // Add this assembly so script can use utilities we provide
                AddAssemblyReference(typeof(UnityMCP.Editor.EditorCommandExecutor).Assembly.Location);

                // Add netstandard assembly
                var netstandardAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "netstandard");
                if (netstandardAssembly != null)
                {
                    AddAssemblyReference(netstandardAssembly.Location);
                }

                // Reference every loaded UnityEngine/Unity module + the project's own
                // scripts + VRChat assemblies, so snippets can use any engine API
                // (e.g. ImageConversion.EncodeToPNG, JsonUtility, ScreenCapture) and call
                // project/editor types (Assembly-CSharp[-Editor], UdonSharp behaviours)
                // without this list needing manual upkeep.
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.IsDynamic) continue;
                    string loc;
                    try { loc = asm.Location; } catch { continue; }
                    if (string.IsNullOrEmpty(loc)) continue;

                    var name = asm.GetName().Name;
                    bool include =
                        name.StartsWith("UnityEngine") ||  // all engine modules incl. ImageConversion
                        name.StartsWith("Unity.") ||        // packages (TextMeshPro, Burst, ...)
                        name.StartsWith("VRC") ||           // VRCSDK3, VRCSDKBase, VRC.Udon, ...
                        name.StartsWith("UdonSharp") ||
                        name == "Assembly-CSharp" ||        // project runtime scripts
                        name == "Assembly-CSharp-Editor";   // project editor scripts (e.g. SsxLevelImporter)

                    if (include) AddAssemblyReference(loc);
                }

                // Debug.Log("Added Assembly References:" + string.Join(", ", options.ReferencedAssemblies));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UnityMCP] Assembly reference setup issue: {e.Message}");
            }

            // Compile and execute
            using (var provider = new Microsoft.CSharp.CSharpCodeProvider())
            {
                var results = provider.CompileAssemblyFromSource(options, code);
                if (results.Errors.HasErrors)
                {
                    //Debug.LogError($"Assembly references: {string.Join(", ", options.ReferencedAssemblies)}");
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
