using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Newtonsoft.Json;
using Microsoft.CSharp;
using System.CodeDom.Compiler;

namespace UnityMCP.Editor
{
    public class EditorCommandExecutor
    {
        public class EditorCommandData
        {
            public string code { get; set; }
        }

        // Compiles and runs the supplied C# on Unity's main thread and returns the result
        // payload ({ result, logs, errors, warnings, executionSuccess, [errorDetails] }). The
        // connection layer serializes this into a "commandResult" message (echoing the request
        // id) and sends it back to the requesting client. Running on the main thread is required
        // because the code touches Unity APIs; RunOnMainThread also defers while the Editor is
        // compiling, so the snippet executes against stable post-compile state.
        public static Task<object> ExecuteAndGetResult(string commandData)
        {
            var commandObj = JsonConvert.DeserializeObject<EditorCommandData>(commandData);
            var code = commandObj?.code;

            return EditorUtilities.RunOnMainThread<object>(() =>
            {
                var logs = new List<string>();
                var errors = new List<string>();
                var warnings = new List<string>();

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
                            var stackLine = stackTrace?.Split('\n')?.FirstOrDefault() ?? "";
                            errors.Add($"{message}\n{stackLine}");
                            break;
                    }
                }

                // Capture logs for the duration of this command only. Because the main-thread
                // queue drains sequentially, commands never execute concurrently, so this scopes
                // cleanly to the single snippet.
                Application.logMessageReceived += LogHandler;
                try
                {
                    Debug.Log("[UnityMCP] Executing code...");
                    var result = CompileAndExecute(code);
                    Debug.Log("[UnityMCP] Code executed");

                    return (object)new
                    {
                        result = result,
                        logs = logs,
                        errors = errors,
                        warnings = warnings,
                        executionSuccess = true
                    };
                }
                catch (Exception e)
                {
                    var firstStackLine = e.StackTrace?.Split('\n')?.FirstOrDefault() ?? "";
                    var error = $"[UnityMCP] Failed to execute editor command: {e.Message}\n{firstStackLine}";
                    Debug.LogError(error);

                    return (object)new
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
                    };
                }
                finally
                {
                    Application.logMessageReceived -= LogHandler;
                }
            });
        }


        public static object CompileAndExecute(string code)
        {
            // No blocking wait-for-compile here. Callers route through
            // EditorUtilities.RunOnMainThread, whose queue already defers while the Editor is
            // compiling, so by the time this runs the domain is stable. (The Script Tester window
            // calls this directly from a user click, which is also never mid-compile.)

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
                try
                {
                    return method.Invoke(null, null);
                }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    // Reflection wraps whatever EditorCommand.Execute() throws in a
                    // TargetInvocationException, whose message ("Exception has been thrown by the
                    // target of an invocation") and stack trace are reflection plumbing, not the real
                    // failure. Rethrow the inner exception - preserving its original stack - so callers
                    // report the actual error (e.g. MissingComponentException) instead of the wrapper.
                    ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                    throw; // unreachable: Throw() above always rethrows; satisfies the compiler.
                }
            }
        }
    }
}
