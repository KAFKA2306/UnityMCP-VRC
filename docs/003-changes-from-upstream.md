# 003 — Changes from the original fork

A record of how this repo diverges from its upstream, [Arodoid/UnityMCP]. It is a
*what changed and why it matters* summary, not a commit-by-commit changelog — for the
current design see [001 — Architecture](001-architecture.md) and the rationale in
[002 — Design decisions](002-design-decisions.md).

[Arodoid/UnityMCP]: https://github.com/Arodoid/UnityMCP

## Fork point

Forked from `Arodoid/UnityMCP` at commit `91d84c1` (2025-03-18, the last upstream
commit). Everything from `1d38606` onward is this fork's work.

## What upstream was

UnityMCP at the fork point was a working but minimal proof of concept:

- **Topology:** the **MCP server** (`unity-mcp-server`) hosted the WebSocket server on a
  fixed port, and the **Unity plugin** dialed in as a WebSocket **client**.
- **Code shape:** two C# files (`UnityMCPConnection.cs`, `UnityMCPWindow.cs`) and a single
  monolithic `unity-mcp-server/src/index.ts` with all tools defined inline.
- **Tools (3):** `get_editor_state` (a continuously streamed snapshot, with Raw / scripts-
  only / no-scripts output filters), `execute_editor_command` (run a C# snippet), and
  `get_logs`.
- **License:** MIT.

## What this fork is

A refactor focused on **multi-session reliability** and **VRChat / UdonSharp** world
building, while staying useful for ordinary Unity work.

| Aspect            | Upstream (`91d84c1`)                          | This fork (current)                                            |
| ----------------- | --------------------------------------------- | -------------------------------------------------------------- |
| Who hosts         | MCP server hosts; Unity connects              | **Unity plugin hosts; MCP servers connect** (many sessions)    |
| WebSocket         | `ws` library / platform                       | **Hand-rolled RFC 6455** over a raw `TcpListener` (Mono fix)   |
| Editor state      | Continuous stream, output filters             | **On-demand, bounded** (caps on objects/nodes/depth)           |
| `execute_…`       | C# snippet, fixed reference list              | **Full C# (classes/usings), auto-discovered references**       |
| Large results     | Unbounded                                     | **Capped + paged** via `get_command_page`                      |
| Recompiles        | Link could hang / go zombie                   | **Survives domain reloads**; in-flight requests fail fast      |
| Plugin code       | 2 files                                       | **Broken into focused files** (connection, executor, etc.)     |
| MCP server code   | 1 monolithic `index.ts`                       | **Thin `index.ts` + one file per tool** + resources            |
| Resources         | —                                             | **MCP text resources** (UdonSharp / VRChat notes)              |
| Tools             | 3                                             | 7 (`get_command_page`, `clear_logs`, `take_screenshot`, `get_object_details` added) |
| License           | MIT                                           | **CC BY-NC 4.0**                                               |

## Changes by area

### Connection model — inverted
The biggest structural change. Unity now hosts a single server and any number of MCP
clients (Claude sessions) attach to the same Editor, instead of each MCP-server process
racing for the port and leaving "connected-but-dead" zombies. Requests carry an `id` that
Unity echoes on the originating socket, enabling reply routing and concurrent in-flight
requests. The WebSocket handshake/framing are hand-rolled over a raw `TcpListener` because
Unity's Mono runtime doesn't reliably implement `HttpListener`'s WebSocket upgrade. Full
rationale in [002 §"Unity hosts the server"](002-design-decisions.md).

### Threading & lifecycle — survives recompiles
- `EditorUtilities.RunOnMainThread` marshals Unity-API work onto a queue drained on
  `EditorApplication.update`, defers while compiling, and times out with an actionable
  message instead of hanging when the Editor is throttled (unfocused).
- A domain reload (any recompile, including ones triggered by `execute_editor_command`)
  tears the link down cleanly via `AssemblyReloadEvents.beforeAssemblyReload`; clients see
  the close immediately and fail in-flight requests with a retry hint, then auto-reconnect.

### `execute_editor_command` — real C#
Authors full C# (its own `using`s, classes, functions), not just one-liners. Assembly
references are **auto-discovered** from all loaded assemblies (UnityEngine/packages,
VRChat/UdonSharp, `Assembly-CSharp`, UnityMCP itself) rather than a hand-maintained list.
Supports commands larger than 4 KB, trims stack traces to the first line to save context,
and caps oversized results behind paging.

### `get_editor_state` — on-demand and bounded
Changed from a continuous stream to an on-demand call, and **capped** for large
scenes/projects (≤300 listed objects/assets, ≤500 hierarchy nodes, depth ≤8) so a dump
can't blow up the context window. Adds Assets/-folder asset listing; drops the upstream
Raw / scripts-only / no-scripts output filters.

### `get_command_page` — new
Pages later slices of an oversized `execute_editor_command` result from a server-side
cache, rather than re-running the command (which could re-fire side effects). See
[002 §"Paging via a server-side snapshot"](002-design-decisions.md).

### `clear_logs` — new
Empties the server-side log buffer (reporting how many entries it dropped) so a later
`get_logs` isn't muddied by stale errors — e.g. a one-off failed compile. Buffer-only, like
`get_command_page`, so it works regardless of connection state.

### `take_screenshot` / `get_object_details` — new
Ported from [setohima/UnityMCP-VRC] and adapted to this branch's inverted, id-correlated
connection. The upstream port pushed unsolicited messages and parked a single global
in-flight promise per result type, which can't serve the many concurrent Claude sessions
this branch supports; here both tools ride the standard `sendRequest`/`SendResponse` path so
each call is matched to its own response by id.
- **`take_screenshot`** renders the Scene view (default) or the game camera to a JPEG/PNG
  image block, for visual iteration on edits.
- **`get_object_details`** returns a GameObject's transform (incl. world-space `lossyScale`),
  tag, layer, children, and per-component fields/properties — plus size info the inspector
  can't easily give (Renderer world-space bounds, mesh vertex counts, shared mesh/material
  names) — so common inspections don't each need hand-written reflection C#.

### VRChat / UdonSharp support — new
- `UdonSharpHelper` compiles C# into UdonSharp assets.
- VRChat / UdonSharp / TextMeshPro and terrain / physics / particle assemblies wired into
  the command compiler.
- **MCP resources:** any file in `unity-mcp-server/src/resources/text/` is exposed as a
  `file:///` resource (UdonSharp and VRChat world notes ship by default) to raise Claude's
  UdonSharp success rate.

### Code organization
- **Plugin** split into `ClientConnection`, `EditorCommandExecutor`, `EditorStateReporter`,
  `EditorUtilities`, `ScriptTesterWindow`, `UdonSharpHelper`, plus the reworked
  `UnityMCPConnection` and `UnityMCPWindow`.
- **MCP server** split into a thin `index.ts`, `communication/UnityConnection.ts`, one
  `tools/*.ts` per tool behind a shared `Tool` interface, a `commandResultCache`, and a
  `resources/` loader. Upgraded the MCP SDK to 1.x.

### Tooling & diagnostics
- Expanded **Debug Window**: server-listening state, attached clients, last request, and
  main-thread queue depth, so a broken link is obvious.
- **Script Tester** window for manually running/diagnosing C# commands.
- Stopped tracking Unity `.meta` files (Unity regenerates them on import).

### License
Relicensed from **MIT** to **Creative Commons Attribution-NonCommercial 4.0
(CC BY-NC 4.0)**.

[setohima/UnityMCP-VRC]: https://github.com/setohima/UnityMCP-VRC
