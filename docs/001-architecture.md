# 001 — Architecture (current state)

## Overview

UnityMCP has two halves:

- **Unity Editor plugin** (`UnityMCPPlugin/Editor`, C#) — hosts a small **HTTP server** inside the
  running Editor and executes work against the Unity API.
- **MCP server** (`unity-mcp-server`, Node/TypeScript) — a stdio MCP server that sends the plugin
  one HTTP request per tool call and exposes the tools to Claude.

```
  Claude session A ──┐   one HTTP request per tool call
  Claude session B ──┤   POST { type, data }  →  JSON result
  Claude session C ──┘
          │  http://localhost:8080
          ▼
   ┌─────────────────────────────┐
   │  Unity Editor plugin        │   hosts the server · one Editor · many clients
   └─────────────────────────────┘
```

Unity hosting the server (rather than each MCP-server process hosting its own) lets many Claude
sessions share one Editor with no port race. The transport is **stateless request/response**; it
began as a persistent WebSocket — see the [historical
note](002-design-decisions.md#historical-the-websocket-transport-we-replaced) for why that's gone.

## Connection model

- **Unity hosts; clients send requests.** A single `TcpListener` on port `8080` (dual-stack
  IPv4/IPv6) accepts connections; each carries **one** HTTP request and is then closed. Because Unity
  owns the one port, any number of Claude sessions drive the same Editor — no race.
- **Request/response.** A tool call is `POST /` with a JSON `{ type, data }` body; the plugin runs it
  and returns the result as the JSON response body. No id-correlation, no persistent connection — the
  response *is* the reply.
- **Logs are pulled.** The plugin keeps a rolling console-log buffer; `get_logs` reads it on demand
  and `clear_logs` empties it. Nothing is pushed from Unity.

### Wire protocol

| Direction       | Message                                                                   |
| --------------- | ------------------------------------------------------------------------- |
| client → Unity  | `POST / {"type":"executeEditorCommand","data":{"code":"…"}}`              |
| Unity → client  | `200 OK` + the result payload as the JSON body                            |
| client → Unity  | `POST /` with type `getEditorState` · `takeScreenshot` · `getGameObjectDetails` · `getLogs` · `clearLogs` |
| Unity → client  | `400`/`500` + `{ "error": … }` for an unknown type or a handler failure   |
| any → Unity     | `GET` / `HEAD /` → `200 {"status":"ok","server":"UnityMCP"}` (liveness check); any other method → `405` |

## Transport: hand-rolled HTTP

The plugin reads the request and writes the response by hand over a raw `TcpListener`, rather than
using `System.Net.HttpListener`. This is necessary, not a preference: **Unity's Mono runtime doesn't
reliably implement `HttpListener`** (it's also why the old WebSocket upgrade had to be hand-rolled).
The implementation is deliberately tiny — read the request line + headers to the blank line, read
`Content-Length` bytes of body, write a `Content-Length` response, close (`Connection: close`, one
request per socket). No keep-alive, no chunked encoding.

## Threading model (Unity side)

> The Editor states these mechanisms cope with — compiling, domain reload, play-mode,
> focus/throttling — are covered in [004 — Unity Editor states](004-unity-editor-states.md).

The accept loop and the short-lived per-request handlers run on the **thread pool** (async), off
Unity's editor loop. Anything that touches a Unity API is marshalled to the main thread via
`EditorUtilities.RunOnMainThread`, which:

- enqueues the work and drains the queue on every `EditorApplication.update` tick;
- **defers while Unity is compiling** (`EditorApplication.isCompiling`), so work runs against stable
  post-compile state instead of racing a recompile;
- has a generous timeout (`MainThreadTimeoutMs`, 55s) but fast-fails sooner when it can: a thread-pool
  watchdog notices the loop hasn't ticked for a few seconds (the Editor throttles its loop while
  **unfocused**) and throws an actionable message rather than waiting out the full timeout (focus the
  Editor, or set *Preferences > General > Interaction Mode > No Throttling*).

Because each connection handles a single request and is closed, there are no long-lived per-client
receive loops or send locks to manage.

## Lifecycle & domain reload

- **Start.** `[InitializeOnLoad]` runs the static constructor on editor load and after every domain
  reload; it starts the server via `EditorApplication.delayCall`.
- **Teardown on reload.** A recompile ends in a domain reload that wipes all managed state. On
  `beforeAssemblyReload` the plugin just stops the listener (and closes any socket with a request in
  flight). There's no persistent connection to drain — which is exactly why this is now a non-event
  (see [002](002-design-decisions.md#historical-the-websocket-transport-we-replaced)).
- **Reconnect.** The static constructor restarts the server in the new domain. A client request that
  lands mid-reload gets connection-refused and **retries** (bounded, ~20s) until the server is back,
  so a recompile is a brief pause, not a failure. A request that was already in flight when the socket
  dropped is *not* retried — it may have applied, so the caller is told to check state.
- **Manual restart.** The Debug Window's *Restart Server* button calls `RetryConnection` (stop +
  start) — useful after freeing a port another process held.

## Tools

| Tool                     | What it does                                                             |
| ------------------------ | ------------------------------------------------------------------------ |
| `execute_editor_command` | Compiles and runs LLM-authored C# in the Editor; returns result + logs.  |
| `get_editor_state`       | Returns Unity/scene/project state on demand (bounded).                   |
| `get_object_details`     | Returns one GameObject's transform, components, and size/bounds info.    |
| `get_logs`               | Returns recent Unity console logs from the plugin's buffer.              |
| `clear_logs`             | Clears the plugin's buffered console logs.                               |
| `take_screenshot`        | Renders the Scene or game camera to a JPEG/PNG image block.              |
| `get_command_page`       | Fetches a later page of a large result (used automatically).             |

- **execute_editor_command.** The LLM authors full C# — its own `using`s, classes, and functions.
  Assembly references are auto-discovered from all loaded assemblies (UnityEngine modules and
  packages, VRChat/UdonSharp, the .NET base class library, `Assembly-CSharp(-Editor)`, and UnityMCP
  itself), so commands can use any available API with no hand-maintained list. Runs on the main thread
  with scoped log capture; stack traces are trimmed to the first line. Results over ~25k chars are
  **capped** and paged via `get_command_page`. Full model: [005 — Executing LLM C#](005-executing-csharp.md).
- **get_editor_state.** On demand (not a stream). **Capped** for large scenes/projects — ≤300 listed
  objects/assets, ≤500 hierarchy nodes, depth ≤8. Oversized results are paged too.
- **get_object_details.** Resolves a GameObject by name or hierarchy path and reports its transform
  (incl. world-space `lossyScale`), tag, layer, children, and per-component fields/properties via
  reflection — plus extras the inspector can't easily give (Renderer world-space bounds, mesh vertex
  counts, shared mesh/material names). Caps collection previews and recursion depth; oversized results
  are paged.
- **take_screenshot.** Renders the Scene view (default) or the game camera to a base64 JPEG/PNG image
  block. Runs on the main thread; subject to the same unfocused-Editor throttling as other calls.
- **get_logs / clear_logs.** Read from / empty the plugin's rolling log buffer (~1000 entries).
- **get_command_page.** Pulls later slices of a cached oversized result by token + offset. Reads only
  the MCP server's in-memory cache, so it's the one tool that works even while Unity is unreachable.

## MCP server (client) internals

- `communication/UnityConnection.ts` sends each tool call as an HTTP POST and returns the JSON
  response. `sendRequest(type, data, timeoutMs)` aborts after `timeoutMs` and **retries a refused
  connection** (bounded, ~20s — `connectWaitMs`), so a call issued during a domain reload pauses for
  the bounce instead of failing. A connection *reset* mid-request is surfaced as *may have applied —
  check state* rather than retried.
- **Lifetime.** Nothing keeps the process alive but its stdio transport, so it exits when the client
  (Claude) goes away — there's no background reconnect timer, hence no lingering "zombie" servers.
- **Resources:** files in `unity-mcp-server/src/resources/text/` are copied into the build and exposed
  as MCP resources (`file:///<name>`).

## Component / file map

**Plugin (`UnityMCPPlugin/Editor`)**

| File                      | Responsibility                                                            |
| ------------------------- | ------------------------------------------------------------------------- |
| `UnityMCPConnection.cs`   | Server lifecycle (bind/accept/teardown), request dispatch, log buffer, debug-window properties. |
| `ClientConnection.cs`     | One HTTP request/response exchange (read request, write response) over a raw socket. |
| `EditorCommandExecutor.cs`| Compile + run LLM C#, scoped log capture, result payload.                |
| `EditorStateReporter.cs`  | Build the bounded editor-state payload.                                  |
| `InspectorDataReporter.cs`| Build a GameObject's component/inspection payload (bounded).             |
| `ScreenshotCapturer.cs`   | Render the Scene/game camera to a base64 image.                          |
| `EditorUtilities.cs`      | Main-thread queue / `RunOnMainThread`, compile-defer, throttle hint.     |
| `UnityMCPWindow.cs`       | Debug Window diagnostics panel.                                          |
| `ScriptTesterWindow.cs`   | Manual command runner for diagnosing C#.                                 |
| `UdonSharpHelper.cs`      | Generate UdonSharp assets from C# (VRChat).                              |

**MCP server (`unity-mcp-server/src`)**

| Path                          | Responsibility                                                       |
| ----------------------------- | ------------------------------------------------------------------- |
| `index.ts`                    | MCP wiring: list/call tools, list/read resources.                   |
| `communication/UnityConnection.ts` | HTTP client: POST a request, return the JSON response, retry a refused connection. |
| `tools/*.ts`                  | One file per tool behind a common `Tool` interface.                 |
| `tools/commandResultCache.ts` | Shared cache + paging for oversized results.                        |
| `resources/*.ts`              | Text-resource loading.                                              |

## Configuration & limits

| Setting                  | Value / location                                                            |
| ------------------------ | --------------------------------------------------------------------------- |
| Port                     | `8080` (`UnityMCPConnection.Port`, `new UnityConnection(8080)`)             |
| Main-thread timeout      | `55s` (`EditorUtilities.MainThreadTimeoutMs`) — raise with the matching per-tool timeout if needed |
| Editor-state caps        | ≤300 objects/assets, ≤500 hierarchy nodes, depth ≤8                        |
| Result response cap      | ~25,000 chars before paging (`MAX_RESPONSE_CHARS`); cache TTL 5 min, 20 entries |
| Log buffer               | ~1000 entries (plugin side)                                                |
| Connect-retry window     | up to ~20s of retrying a refused connection, then fail with a hint (`connectWaitMs`, client) |

## Known limitations / possible next steps

- Binds all interfaces (dual-stack) — could restrict to loopback if LAN exposure matters. There's no
  auth and `execute_editor_command` runs arbitrary C#, so don't expose the port to untrusted networks
  (see [005 — trust model](005-executing-csharp.md#trust-model)).
- Fixed port; no negotiation if `8080` is taken by something else.
- Logs are pulled, not streamed — fine for how Claude uses them; a long-poll endpoint could add
  near-real-time if ever needed.
