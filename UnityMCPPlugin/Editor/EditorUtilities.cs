using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace UnityMCP.Editor
{
    public static class EditorUtilities
    {
        // How long a main-thread request waits before giving up. Unity throttles (and on
        // some platforms effectively suspends) its editor loop while the window is unfocused,
        // so this is intentionally generous: a queued request completes whenever Unity next
        // ticks (e.g. when you refocus the Editor) rather than erroring out early. Raise this
        // AND the matching per-tool timeout in the *.ts tools if you want to wait even longer.
        public const int MainThreadTimeoutMs = 55000;

        // Work that must run on Unity's main thread, drained on every editor tick.
        private static readonly ConcurrentQueue<Action> mainThreadQueue = new ConcurrentQueue<Action>();
        private static bool loggedThrottleHint = false;

        [InitializeOnLoadMethod]
        private static void RegisterMainThreadPump()
        {
            // Guard against double-registration across domain reloads.
            EditorApplication.update -= DrainMainThreadQueue;
            EditorApplication.update += DrainMainThreadQueue;
        }

        private static void DrainMainThreadQueue()
        {
            // Defer while Unity is compiling so queued work runs against a stable, post-compile
            // state and returns a real result instead of racing the recompile. (Script recompiles
            // end in a domain reload that resets this queue; those in-flight requests are lost and
            // must be retried by the caller.)
            if (EditorApplication.isCompiling)
                return;

            while (mainThreadQueue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception e) { Debug.LogError($"[UnityMCP] Main-thread action failed: {e.Message}"); }
            }
        }

        /// <summary>
        /// Runs <paramref name="func"/> on the Unity main thread and returns its result.
        /// The work is queued and drained from EditorApplication.update, so it executes as soon
        /// as Unity next ticks (and is held back while Unity is compiling, running once that
        /// finishes). While the Editor is unfocused Unity throttles that loop, so a
        /// call may wait until you refocus the window (or set Preferences > General >
        /// Interaction Mode to "No Throttling"). If it still has not run within
        /// <paramref name="timeoutMs"/> it throws a TimeoutException with an actionable message.
        /// The wait runs off the main thread (ConfigureAwait(false)) so it is not itself blocked
        /// by the throttled loop.
        /// </summary>
        public static async Task<T> RunOnMainThread<T>(Func<T> func, int timeoutMs = MainThreadTimeoutMs)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            mainThreadQueue.Enqueue(() =>
            {
                try { tcs.SetResult(func()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
            if (completed != tcs.Task)
            {
                if (!loggedThrottleHint)
                {
                    loggedThrottleHint = true;
                    Debug.LogWarning("[UnityMCP] A request waited for the main thread without it running. " +
                        "To let MCP tools work while the Editor is unfocused, try " +
                        "Preferences > General > Interaction Mode > No Throttling.");
                }
                throw new TimeoutException(
                    $"Unity main thread did not run within {timeoutMs / 1000f:0.#}s. " +
                    "The Editor is likely unfocused or compiling - focus the Unity Editor window " +
                    "(or set Preferences > General > Interaction Mode to 'No Throttling') and retry.");
            }

            // Awaiting the completed task returns its value or rethrows the inner exception.
            return await tcs.Task.ConfigureAwait(false);
        }
    }
}
