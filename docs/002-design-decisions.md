# 002 — Design decisions

The non-obvious *why* behind the architecture in [001](001-architecture.md). Each entry is the
problem, the call we made, and the trade-off — not implementation detail, which lives in 001.

## Unity hosts the server, one request per call

**Context.** Two processes have to talk: the Unity Editor and one MCP server per Claude session.
Originally each MCP server *was* a WebSocket server on a fixed port and Unity dialed in — but every
session spawns its own MCP server, so they fought over the port (only the first bound it; the rest
ran as zombies that looked connected to their Claude yet could never reach Unity).

**Decision.** Invert it and keep it stateless: the **Unity plugin hosts** one server and each MCP
server **sends an HTTP request per tool call**. Unity owns the single port, so any number of sessions
share one Editor with no race. Request/response needs no id-correlation (the response is the reply)
and no persistent connection — which matters most across domain reloads (the historical note below).
The HTTP is hand-rolled over a raw `TcpListener` because Unity's Mono doesn't reliably implement
`HttpListener`.

**Trade-off.** We own a small HTTP read/write instead of leaning on the platform, and we give up
server-initiated push — neither of which we actually needed (see below).

## Paging via a server-side snapshot, not re-running

**Context.** `execute_editor_command` returns whatever the command produces, with no bound — "list
every GameObject" can be hundreds of KB and blow out the context window. The shape-aware tools cap at
the source, but those caps bound *breadth and depth*, not total bytes — a wide result (e.g. a renderer
with 97 sub-mesh materials) can still overflow.

**Decision.** Cap the returned text (~25k chars). On overflow, cache the *full* result in the MCP
server under a token and return page 1 plus a footer; `get_command_page` serves later slices from the
cache rather than re-running the command (which could re-fire side effects, and even read-only queries
drift when ordering isn't stable). One shared helper (`pageText`) gives `execute_editor_command`,
`get_editor_state`, and `get_object_details` the same byte-level backstop on top of their source caps.

**Trade-off.** This protects the *model's context window*, not the wire — the full payload still
crosses once and lives briefly in server RAM. The cache lives in the MCP server (not the plugin)
precisely so a domain reload can't wipe it mid-paging.

## Historical: the WebSocket transport we replaced

The transport began as a **hand-rolled WebSocket** server in the plugin. (Hand-rolled because Unity's
Mono doesn't reliably implement `HttpListener`'s WebSocket upgrade — the same reason we still
hand-roll plain HTTP today.) It's gone now, for two reasons worth recording:

- **We never needed server→client push.** The only thing Unity ever pushed was console-log streaming,
  and logs are pulled on demand anyway (`get_logs`); command results are just replies. So the one
  capability a WebSocket has over request/response went unused.
- **Cleaning up the sockets is what hung domain reloads.** A persistent socket must be torn down on
  every reload, and a managed thread parked in a native socket op blocks the domain from unloading
  until the OS times out. On Mono that teardown proved riddled with edge cases — a mid-handshake
  socket, a reconnect storm from accumulated clients, a wedged client whose send never drained — each
  stalling reloads for *minutes* (we saw 126s and 519s). Each fix (track every accepted socket,
  bounded sends, abortive close, a teardown flag) removed one case and exposed the next.

Stateless request/response sidesteps all of it: there's no idle socket between calls, so a reload has
nothing to drain — it stops the listener, and the next request retries. Removing that entire class of
reload-teardown bugs is the real reason for the switch.
