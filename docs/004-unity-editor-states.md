# 004 — Unity Editor states

Background on the Editor lifecycle this project has to survive: the states the Editor moves
through, when each happens, and what each means for a command in flight. The mechanics of how the
plugin *responds* live in [001 — Architecture](001-architecture.md); the *why* behind those
choices is in [002 — Design decisions](002-design-decisions.md). This doc is the Unity-side
facts those build on.

## The baseline: one main thread

Unity drives the Editor from a single main-thread tick, surfaced as `EditorApplication.update`.
**Anything that touches the Unity API must run on that thread** — which is why the plugin marshals
every command through `EditorUtilities.RunOnMainThread` and drains its queue on each tick.

Background **threads and Tasks** (the HTTP accept loop and short-lived request handlers) run
independently of that loop and keep going even when it doesn't tick. That split is the key to
everything below: *a request can be mid-flight while the main thread is frozen, and the main thread
can be ticking while the listener is gone.* The two failure modes are independent.

## 1. Compiling — `EditorApplication.isCompiling == true`

**When it happens**

- A `.cs` file under `Assets/` (or an in-project package) is added/changed/deleted **and Unity
  refreshes** — by default when the Editor **regains focus**, or on a manual/scripted
  `AssetDatabase.Refresh()`, or per *Preferences → Asset Pipeline → Auto Refresh*.
- An `.asmdef`/`.asmref` changes, scripting define symbols change, or
  `CompilationPipeline.RequestScriptCompilation()` is called.

**What it means.** Compilation runs in the background (Roslyn, off-thread). The editor loop
**still ticks** and the socket stays up — but the freshly built assemblies aren't loaded yet, so
running code now would race the recompile. If compilation **fails**, there is *no* reload:
`isCompiling` goes false, you stay in the old domain, and your scripts aren't updated. A
*successful* compile in edit mode is followed by a domain reload (§2).

**Plugin behavior.** `DrainMainThreadQueue` returns early while `isCompiling`, so a queued command
**waits** rather than running against half-built state.

## 2. Domain reload (assembly reload)

The managed scripting **AppDomain is torn down and rebuilt** — the single most disruptive event.

**When it happens**

- After a **successful recompile** in edit mode (to load the new assemblies).
- **Entering** *and* **exiting** Play Mode — by default (configurable; see §3).
- Opening/switching projects, switching build target, or `EditorUtility.RequestScriptReload()`.

**What it means — it wipes all managed state**

- Every `static` field resets, event subscriptions are lost, **running threads/Tasks are
  abandoned**, and in-memory objects that aren't serialized are gone.
- Static initializers re-run: `[InitializeOnLoad]` / `[InitializeOnLoadMethod]` fire again in the
  fresh domain.
- Bracketed by `AssemblyReloadEvents.beforeAssemblyReload` (last synchronous chance to clean up or
  persist) and `afterAssemblyReload`.
- To survive a reload, state must be **serialized** — `SessionState` / `EditorPrefs`, a
  `ScriptableSingleton`, or an asset on disk. (This is why the paging cache lives in the MCP
  server process, not the plugin — see [002](002-design-decisions.md).)

**Plugin behavior.** `beforeAssemblyReload` just stops the listener (and closes any socket with a
request in flight) — there's no persistent connection to drain. The `[InitializeOnLoad]` static
constructor re-runs in the new domain and restarts the server; a client request that lands mid-reload
is refused and retries until it's back.

## 3. Play Mode transitions

`EditorApplication.playModeStateChanged` walks `ExitingEditMode → EnteredPlayMode` and
`ExitingPlayMode → EnteredEditMode`. **By default each crossing triggers a domain reload** (plus a
scene reload), so entering/leaving Play Mode looks exactly like §2 to the plugin: socket drops,
server restarts, reconnect.

**Configurable.** *Project Settings → Editor → Enter Play Mode Settings* can disable "Reload
Domain" (and "Reload Scene") for faster iteration. With domain reload off, entering Play Mode does
**not** tear the domain down, so the link stays up.

## 4. Focus / throttling

**When it happens.** The Editor window loses OS focus. By default Unity **throttles**
`EditorApplication.update` (and on some platforms effectively pauses it). *Preferences → General →
Interaction Mode → No Throttling* keeps it ticking in the background.

**What it means.** The socket and background threads stay alive, but the **main-thread loop
stalls**, so queued Unity work can't run. This is *not* a disconnect — the link is fine; the loop
is just asleep.

**Plugin behavior.** The stall detector runs on the thread pool (not the frozen loop), so
`RunOnMainThread` notices no ticks for a few seconds and fast-fails with an "unfocused or busy"
message instead of hanging out the full main-thread timeout.

## 5. Busy (synchronous main-thread work)

A modal dialog, a progress bar (`EditorUtility.DisplayProgressBar`), a large
`AssetDatabase.Refresh`/import, or any long synchronous operation occupies the main thread. The
socket stays up, but the loop doesn't tick until the operation returns — indistinguishable from §4
to a waiting command, and handled the same way (it waits, then fast-fails if the stall persists).

## The typical "edit a script" timeline

```
command writes a .cs file  ──►  (Editor refreshes: on focus / AssetDatabase.Refresh)
   └► isCompiling = true    ──►  background compile  ──►  success
        └► beforeAssemblyReload   (plugin: StopServer — stop the listener)
             └► domain UNLOAD     (statics wiped, sockets gone, Tasks abandoned)
                  └► domain LOAD  ──►  afterAssemblyReload, [InitializeOnLoad] re-runs
                       └► server restarts  ──►  next request reconnects
```

The command that *wrote* the file usually finishes and returns **before** this chain starts; it's
the **next** command that races the restart window — refused, then retried automatically. A command
interrupted *mid-flight* (connection reset) is the ambiguous one: it may have applied, so it isn't
auto-retried.

## Implications at a glance

| State                         | Listener | Main-thread loop | Command outcome                                            |
| ----------------------------- | -------- | ---------------- | ---------------------------------------------------------- |
| Idle / editing                | up       | ticking          | runs immediately                                           |
| **Compiling**                 | up       | ticking          | **deferred** until the compile finishes                    |
| **Domain reload**             | down     | torn down        | in-flight request dropped; the next request retries until the server restarts |
| **Play-mode enter/exit** (default) | down | torn down   | same as a domain reload                                    |
| **Unfocused / throttled**     | up       | frozen           | stalls → fast-fail ("unfocused or busy")                   |
| **Busy** (modal / import)     | up       | blocked          | stalls → waits, then fast-fail if it persists              |

## Surviving a reload (state persistence)

If you need a value to outlive a domain reload, you must serialize it — static fields and plain
in-memory objects do not survive. Options, cheapest first:

- `SessionState` — string/int/bool/vector, keyed, **cleared when the Editor quits**. Good for
  "remember across reloads within this session."
- `EditorPrefs` — same shape, **persists across Editor restarts** (per-user, machine-global).
- `ScriptableSingleton<T>` — a serialized object with arbitrary `[SerializeField]` fields,
  saved to `Library/`.
- An asset (`ScriptableObject` written under `Assets/`) — for project-scoped data that should be
  versioned.

The plugin sidesteps all of this for command results by keeping the cache in the MCP server
process (which is *not* reloaded), so a domain reload never loses it.
