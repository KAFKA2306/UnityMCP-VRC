# 001 — Architecture (current state)

## Overview

UnityMCP has two halves:

- **Unity Editor plugin** (`UnityMCPPlugin/Editor`, C#) — hosts a WebSocket **server**
  inside the running Editor and executes work against the Unity API.
- **MCP server** (`unity-mcp-server`, Node/TypeScript) — a stdio MCP server that
  connects to the plugin as a WebSocket **client** and exposes tools to Claude.

```
  Claude session A ──┐   WebSocket clients
  Claude session B ──┤   (each request tagged with an id)
  Claude session C ──┘
          │  ws://localhost:8080
          ▼
   ┌─────────────────────────────┐
   │  Unity Editor plugin        │   hosts the server · one Editor · many clients
   └─────────────────────────────┘
```

The Unity-as-server topology is **inverted** from the original (where each MCP server
process hosted its own server on a fixed port). The full rationale is in
[design decisions](002-design-decisions.md); the short version is below.

## Connection model

- **Unity hosts; clients connect.** A single `TcpListener` on port `8080` (dual-stack
  IPv4/IPv6) accepts any number of clients. Because Unity owns the one port, multiple
  Claude sessions can drive the same Editor at once — there is no port race and no
  "connected-looking but dead" zombie server (the failure mode the inversion removed).
- **Request ids / multiplexing.** Every request is `{ type, id, data }`. Unity echoes
  the `id` on the response and replies on the **originating socket**, so each client
  matches replies to its own calls and never sees another client's responses. This also
  allows multiple concurrent in-flight requests per client.
- **Logs are broadcast.** Unity pushes every console log to all connected clients as a
  fire-and-forget event (no id). Each MCP server keeps its own log buffer, served by
  `get_logs`.

### Wire protocol

| Direction              | Message                                                              |
| ---------------------- | ------------------------------------------------------------------- |
| client → Unity (req)   | `{ "type": "executeEditorCommand", "id": "1", "data": { "code" } }` |
| client → Unity (req)   | `{ "type": "getEditorState", "id": "2", "data": {} }`               |
| Unity → client (resp)  | `{ "type": "commandResult", "id": "1", "data": { ... } }`           |
| Unity → client (resp)  | `{ "type": "editorState", "id": "2", "data": { ... } }`             |
| Unity → all (event)    | `{ "type": "log", "data": { message, stackTrace, logType, ts } }`   |
| Unity → all (event)    | `{ "type": "reloading" }` — sent just before a domain-reload teardown |

## Transport: hand-rolled WebSocket

The plugin implements the WebSocket upgrade handshake (SHA-1 of `Sec-WebSocket-Key` +
the RFC 6455 magic GUID) and frame read/write over a raw `TcpListener`, rather than
using `System.Net.HttpListener`.

This is necessary, not a preference: **Unity's Mono runtime does not reliably implement
`HttpListener`'s WebSocket upgrade** (`IsWebSocketRequest` / `AcceptWebSocketAsync`). It
accepts the TCP/HTTP request but silently rejects the upgrade, so every client connect
was being closed immediately. The raw socket sidesteps that entirely.

The framing in `ClientConnection.cs` handles masking (clients mask, server frames are
unmasked), fragmentation, ping/pong, close, and the 64-bit (`127`) length path so large
payloads (big commands, big state dumps) aren't truncated. It does **not** negotiate
`permessage-deflate` — the `ws` client offers it; the plugin omits it from the 101
response, so frames stay uncompressed both ways.

## Threading model (Unity side)

> The Editor states these mechanisms cope with — compiling, domain reload, play-mode,
> focus/throttling — and when each happens are covered in
> [004 — Unity Editor states](004-unity-editor-states.md).

The accept loop and per-socket receive loops run on the **thread pool** (async), off
Unity's editor loop. Anything that touches a Unity API must be marshalled to the main
thread via `EditorUtilities.RunOnMainThread`, which:

- enqueues the work and drains the queue on every `EditorApplication.update` tick;
- **defers while Unity is compiling** (`EditorApplication.isCompiling`), so work runs
  against stable post-compile state instead of racing a recompile;
- has a generous timeout (`MainThreadTimeoutMs`, 55s). The Editor throttles (and on some
  platforms effectively suspends) its loop while **unfocused**, so a queued request may
  not run until you refocus the window. On timeout it throws an actionable message
  (focus the Editor, or set *Preferences > General > Interaction Mode > No Throttling*).

A WebSocket forbids overlapping `SendAsync` calls on one socket, and a response can race
a log broadcast, so each `ClientConnection` wraps its writes in a `SemaphoreSlim(1,1)`.

## Lifecycle & domain reload

- **Start.** `[InitializeOnLoad]` runs the static constructor on editor load and after
  every domain reload; it starts the server via `EditorApplication.delayCall`.
- **Teardown on reload.** A recompile (including edits made by `execute_editor_command`)
  ends in a domain reload that wipes all managed state — the listener, every client
  socket, and the main-thread queue. `AssemblyReloadEvents.beforeAssemblyReload` first
  broadcasts a `reloading` notice to every client (a best-effort *blocking* send, since the
  domain is unloading and async writes might not flush), then calls `StopServer`: cancel the
  token, **close every accepted socket — including any still mid-handshake** — and stop the
  listener. Closing (not the token) is what unblocks a socket read on Mono, so a socket that isn't
  a finished client yet must still be tracked and closed or it pins a thread and stalls the reload
  (see [design decisions](002-design-decisions.md)). A client that received the notice reports
  its still-pending requests as *dropped before running — safe to retry* (they were only queued;
  the loop defers while compiling, then the reload wipes the queue); a close **without** a
  preceding notice is an arbitrary drop, reported as *may have applied — check state*. Either way
  requests fail fast instead of waiting out a timeout; requests not yet sent wait for the
  reconnect instead of failing.
- **Reconnect.** The static constructor restarts the server in the new domain; the MCP
  client reconnects (~3s poll), so a recompile is a brief blip rather than a hang.
- **Manual restart.** The Debug Window's *Restart Server* button calls `RetryConnection`
  (stop + start) — useful after freeing a port another process held.

## Tools

| Tool                     | What it does                                                              |
| ------------------------ | ------------------------------------------------------------------------ |
| `execute_editor_command` | Compiles and runs LLM-authored C# in the Editor; returns result + logs.  |
| `get_editor_state`       | Returns Unity/scene/project state on demand (bounded).                   |
| `get_logs`               | Returns recent Unity console logs from the client's buffer.              |
| `get_command_page`       | Fetches a later page of a large `execute_editor_command` result.         |

- **execute_editor_command.** The LLM authors full C# — its own `using`s, classes, and
  functions — so commands aren't limited to one-liners. Assembly references are
  auto-discovered from all loaded assemblies (UnityEngine modules and packages, VRChat /
  UdonSharp, .NET Standard / System.Core / mscorlib, `Assembly-CSharp(-Editor)`, and
  UnityMCP itself), so commands can use any available API with no hand-maintained list.
  Stack traces are trimmed to the first line to save context. Runs on the main thread
  with scoped log capture. Results over ~25k chars are **capped**: the full result is
  cached and returned page-by-page via `get_command_page` (rationale in
  [design decisions](002-design-decisions.md)).
- **get_editor_state.** On demand (not a continuous stream). **Capped** for large
  scenes/projects — ≤300 listed objects/assets, ≤500 hierarchy nodes, depth ≤8 — so a
  dump can't blow up the context window.
- **get_logs.** Served from the client-side buffer that the plugin's broadcast feeds.
- **get_command_page.** Pulls later slices of a cached oversized command result by token +
  offset. Reads only the in-memory cache, so it works even while Unity is disconnected.

## MCP server (client) internals

- `communication/UnityConnection.ts` is a **reconnecting WebSocket client**: dials
  `ws://localhost:8080`, reconnects every ~3s on drop (covers Unity not yet running and
  domain reloads), and logs `ECONNREFUSED` only once to avoid noise.
- It holds an **id-keyed pending-request map**; `sendRequest(type, data, timeoutMs)`
  tags the request with the next id, sends it, and resolves when the correlated response
  arrives (or rejects on timeout). On socket close it rejects all pending with a retry
  hint.
- `sendRequest` waits (bounded, ~20s — `connectWaitMs`) for a live socket before sending, so a
  call issued during a domain reload pauses for the reconnect instead of failing; past that it
  fails with a hint that the Editor is down/busy. A request already in flight when the socket
  dropped is **not** silently resent: if Unity sent the `reloading` notice first, the request was
  only queued and never ran, so it's reported as *safe to retry*; otherwise it's an arbitrary drop
  reported as *may have applied — check state*, since the command could be non-idempotent (see
  [design decisions](002-design-decisions.md)). Cache/buffer-only tools (`get_command_page`,
  `get_logs`) don't call `sendRequest`, so they work regardless of connection state.
- **Resources:** files in `unity-mcp-server/src/resources/text/` are copied into the
  build and exposed as MCP resources (`file:///<name>`), read at server start.

## Component / file map

**Plugin (`UnityMCPPlugin/Editor`)**

| File                      | Responsibility                                                        |
| ------------------------- | --------------------------------------------------------------------- |
| `UnityMCPConnection.cs`   | Server lifecycle (bind/accept/teardown), client list, dispatch, log broadcast, debug-window properties. |
| `ClientConnection.cs`     | One client's WebSocket wire protocol (handshake + RFC 6455 framing) and per-socket send lock. |
| `EditorCommandExecutor.cs`| Compile + run LLM C#, scoped log capture, result payload.             |
| `EditorStateReporter.cs`  | Build the bounded editor-state payload.                               |
| `EditorUtilities.cs`      | Main-thread queue / `RunOnMainThread`, compile-defer, throttle hint.  |
| `UnityMCPWindow.cs`       | Debug Window diagnostics panel.                                       |
| `ScriptTesterWindow.cs`   | Manual command runner for diagnosing C#.                              |
| `UdonSharpHelper.cs`      | Generate UdonSharp assets from C# (VRChat).                           |

**MCP server (`unity-mcp-server/src`)**

| Path                          | Responsibility                                          |
| ----------------------------- | ------------------------------------------------------- |
| `index.ts`                    | MCP wiring: list/call tools, list/read resources, retry-on-disconnected. |
| `communication/UnityConnection.ts` | Reconnecting WS client + id-keyed pending map.     |
| `tools/*.ts`                  | One file per tool behind a common `Tool` interface.     |
| `tools/commandResultCache.ts` | Shared cache + paging for oversized command results.    |
| `resources/*.ts`              | Text-resource loading.                                  |

## Configuration & limits

| Setting                  | Value / location                                              |
| ------------------------ | ------------------------------------------------------------ |
| Port                     | `8080` (`UnityMCPConnection.Port`, `new UnityConnection(8080)`) |
| Main-thread timeout      | `55s` (`EditorUtilities.MainThreadTimeoutMs`) — raise with the matching per-tool timeout if needed |
| Editor-state caps        | ≤300 objects/assets, ≤500 hierarchy nodes, depth ≤8         |
| Command response cap     | ~25,000 chars before paging (`MAX_RESPONSE_CHARS`); cache TTL 5 min, 20 entries |
| Log buffer               | 1000 entries (plugin side), mirrored per-client             |
| Reconnect poll           | ~3s (client)                                                 |
| Connect-wait before send | up to ~20s, then fails with a hint (`connectWaitMs`, client) |

## Known limitations / possible next steps

- Binds all interfaces (dual-stack), matching the old `ws` default — could restrict to
  loopback if LAN exposure matters.
- Fixed port; no negotiation if `8080` is taken by something else.
- The Debug Window lists each client's remote endpoint but not a human-friendly identity
  (which Claude/project). Clients could send a `hello` with a label on connect.
- No `permessage-deflate` (frames uncompressed both ways).
- No server-initiated heartbeat — the server answers client pings but doesn't actively
  ping. Low value over localhost; if added it must run off the editor loop, not on
  `EditorApplication.update` (see [design decisions](002-design-decisions.md)).
