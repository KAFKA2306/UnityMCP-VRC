# 002 — Design decisions

The non-obvious *why* behind the architecture in [001](001-architecture.md). Each entry is
the problem, the call we made, and the trade-off we accepted — not implementation detail,
which lives in 001.

## Unity hosts the server (inverted connection)

**Context.** Originally each MCP server process *was* the WebSocket server on a fixed port
8080, and Unity dialed in. But every Claude session spawns its own MCP server, so they
fought over the port: only the first bound it; the rest kept running as zombies that looked
"connected" to their Claude (the stdio handshake succeeds regardless) yet could never reach
Unity. A single global pending-promise also limited each tool to one in-flight request.

**Decision.** Invert it — the Unity plugin hosts one server and MCP servers connect as
clients, so any number of Claude sessions share one Editor. Requests carry an `id` that
Unity echoes back on the originating socket, which both routes replies to the right client
and allows concurrent in-flight requests. The handshake/framing are hand-rolled over a raw
`TcpListener` because Unity's Mono runtime doesn't reliably implement `HttpListener`'s
WebSocket upgrade (it accepts the request, then silently drops the upgrade).

**Trade-off.** We own a small WebSocket framing implementation instead of leaning on the
platform — accepted, because it removed the entire port-race/zombie class of bug and the
single-in-flight limitation in one move.

## Paging via a server-side snapshot, not re-running

**Context.** `execute_editor_command` returns whatever the command produces, with no bound —
"list every GameObject" can be hundreds of KB and blow out the context window. The other
tools cap at the source because they know their result shape; a generic command can't.

**Decision.** Cap the returned text (~25k chars). On overflow, cache the *full* result
server-side under a token and return page 1 plus a footer; a companion `get_command_page`
tool serves later slices. Paging slices the cached snapshot rather than re-running the
command with skip/take — because commands can have side effects (re-firing them is unsafe),
and even read-only queries drift when collection ordering isn't stable. Pages are raw text
slices ("concatenate, then parse") rather than per-element JSON, which keeps it general.

**Trade-off.** This protects the *model's context window*, not the wire or Unity memory — the
full payload still crosses the socket once and lives briefly in server RAM. Doing it in C#
would spare those too, but a plugin-side cache is wiped by every domain reload and needs a
protocol change; not worth it for the actual goal.

## Riding out a domain reload (wait, but don't resend)

**Context.** Writing a `.cs` file from a command triggers a recompile and domain reload, which
drops the WebSocket mid-session. A command issued in that window would otherwise fail with a
bare "not connected," forcing the caller to notice and retry — painful in a workflow where
nearly every script edit causes a reload.

**Decision.** `sendRequest` waits (bounded, ~20s) for the socket to come back before sending,
so a not-yet-sent request just pauses for the reconnect and then goes through. But a request
that was *already on the wire* when the socket dropped is **not** silently resent — silently
re-running it could double-apply a non-idempotent edit. To sharpen that case, Unity broadcasts a
`reloading` notice just before it closes the sockets: that fires on the main thread, so nothing
is mid-execution, which means a request still pending when the socket then closes was only
*queued* and never ran. The client reports those as *safe to retry*, reserving the pessimistic
*may have applied — check state* wording for a drop with **no** such notice.

**Trade-off.** The common case recovers transparently (the command that *triggers* the reload
finishes first, and the *next* one races the reconnect — so it never ran and is safe to send).
The genuinely ambiguous case shrinks to an *un-announced* drop (Editor killed, network blip, or
the narrow window where a command ran but its response was lost before the notice) — there we
stay pessimistic and ask the caller to check state. We also accept a ~20s ceiling: beyond it we
assume the Editor is down/busy and fail with a hint rather than hang.

## No server-initiated heartbeat

**Context.** Inverting the connection already delivered most of what a planned
background/lifecycle pass would have: the server's receive loops run off the editor loop,
commands marshal through the main-thread queue, and the port-conflict class is gone. The one
piece left was an active heartbeat.

**Decision.** Don't add one. Over localhost it's low value — the server answers client pings,
TCP delivers RST/FIN when a peer dies, and the client's ~3s reconnect covers drops.

**Trade-off / caveat.** If a heartbeat is ever needed (e.g. to detect a half-open socket
after a machine sleep), it must run **off** the editor loop — a background timer on the
server, or client-side pings — never on `EditorApplication.update`, which throttles when the
Editor is unfocused and would manufacture false disconnects on refocus.
