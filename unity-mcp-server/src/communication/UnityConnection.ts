import { WebSocket } from "ws";
import { LogEntry } from "../tools/index.js";

interface PendingRequest {
  resolve: (value: any) => void;
  reject: (reason: any) => void;
  timer: NodeJS.Timeout;
}

// The Unity Editor now HOSTS the WebSocket server; this MCP server connects to it as a client.
// (Previously this was the server and Unity dialed in, which made multiple MCP servers fight
// over port 8080 - only one won and the rest became unusable zombies.) As a client, any number
// of MCP servers can connect to the one Editor. Each request we send carries a unique "id";
// Unity echoes it back on its response so we can match the reply to the right in-flight call,
// which also lets multiple requests be outstanding at once.
export class UnityConnection {
  private url: string;
  private ws: WebSocket | null = null;
  private connected = false;
  private shuttingDown = false;
  private reconnectTimer: NodeJS.Timeout | null = null;
  private readonly reconnectIntervalMs = 3000;
  private loggedConnectError = false;

  // Set when Unity sends a "reloading" notice just before it drops the socket for a domain reload.
  // Lets the close handler tell a clean reload (request was dropped before running - safe to retry)
  // from an arbitrary disconnect (an in-flight command may have applied). Reset on each connect.
  private reloadAnnounced = false;

  private logBuffer: LogEntry[] = [];
  private readonly maxLogBufferSize = 1000;

  // In-flight requests keyed by id, resolved when Unity sends the matching response.
  private pending = new Map<string, PendingRequest>();
  private nextId = 1;

  // Callers parked in waitForConnection, released on the next "open". Lets a request issued during
  // a domain reload pause for the reconnect instead of failing outright.
  private connectionWaiters: Array<{
    resolve: () => void;
    reject: (reason: Error) => void;
    timer: NodeJS.Timeout;
  }> = [];
  // How long sendRequest waits for a (re)connection before giving up. Covers a normal recompile/
  // domain-reload bounce; beyond this we assume the Editor is down/busy and fail with a hint.
  private readonly connectWaitMs = 20_000;

  constructor(port: number = 8080) {
    this.url = `ws://localhost:${port}`;
    this.connect();
  }

  private connect(): void {
    if (this.shuttingDown) return;

    const ws = new WebSocket(this.url);
    this.ws = ws;

    ws.on("open", () => {
      this.connected = true;
      this.loggedConnectError = false;
      this.reloadAnnounced = false;
      console.error(`[Unity MCP] Connected to Unity Editor at ${this.url}`);
      if (this.reconnectTimer) {
        clearTimeout(this.reconnectTimer);
        this.reconnectTimer = null;
      }
      // Release anything that was waiting for the link to come back.
      this.resolveConnectionWaiters();
    });

    ws.on("message", (data: Buffer) => {
      try {
        const message = JSON.parse(data.toString());
        this.handleUnityMessage(message);
      } catch (error) {
        console.error("[Unity MCP] Error parsing message:", error);
      }
    });

    ws.on("error", (error: Error) => {
      // "close" always follows "error", so reconnect is scheduled there. Log the first
      // connect failure only, to avoid spamming stderr while Unity is starting up.
      const msg = error.message || String(error);
      const isRefused = msg.includes("ECONNREFUSED");
      if (!isRefused || !this.loggedConnectError) {
        console.error("[Unity MCP] WebSocket error:", msg);
        if (isRefused) this.loggedConnectError = true;
      }
    });

    ws.on("close", () => {
      if (this.connected) {
        console.error("[Unity MCP] Disconnected from Unity Editor");
      }
      this.connected = false;
      this.ws = null;

      // Word the failure by whether Unity announced a reload just before closing.
      // Announced: these requests were queued but dropped before running (the editor loop defers
      // while compiling, then the reload wipes the queue), so they never applied - safe to retry.
      // Not announced: an arbitrary drop where an in-flight command may have run, so the caller
      // should check state first. (Never-sent requests wait out the reconnect in waitForConnection
      // and never reach this path.)
      const reason = this.reloadAnnounced
        ? new Error(
            "Unity is reloading its app domain (recompile); your request was dropped before it " +
              "ran - safe to retry once it reconnects (which happens automatically).",
          )
        : new Error(
            "Unity disconnected mid-request (it began recompiling or reloading its app domain). " +
              "The command may or may not have applied - check the editor/scene state before retrying.",
          );
      this.reloadAnnounced = false;
      this.rejectPending(reason);
      this.scheduleReconnect();
    });
  }

  private scheduleReconnect(): void {
    if (this.shuttingDown || this.reconnectTimer) return;
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      this.connect();
    }, this.reconnectIntervalMs);
  }

  private handleUnityMessage(message: any): void {
    const { type, id, data } = message;

    // Correlated response to one of our requests (commandResult / editorState).
    if (id && this.pending.has(id)) {
      const pending = this.pending.get(id)!;
      clearTimeout(pending.timer);
      this.pending.delete(id);
      pending.resolve(data);
      return;
    }

    switch (type) {
      case "log":
        this.handleLogMessage(data);
        break;

      case "reloading":
        // Unity is about to reload its app domain and will drop the socket next. Remember it so the
        // "close" handler reports any still-pending requests as dropped-before-running / safe to retry.
        this.reloadAnnounced = true;
        break;

      case "commandResult":
      case "editorState":
      case "screenshot":
      case "objectDetails":
        // Response arrived with no matching pending request (already timed out / cleared).
        break;

      default:
        console.error("[Unity MCP] Unknown message type:", type);
    }
  }

  private handleLogMessage(logEntry: LogEntry): void {
    this.logBuffer.push(logEntry);
    if (this.logBuffer.length > this.maxLogBufferSize) {
      this.logBuffer.shift();
    }
  }

  private rejectPending(reason: Error): void {
    for (const pending of this.pending.values()) {
      clearTimeout(pending.timer);
      pending.reject(reason);
    }
    this.pending.clear();
  }

  // Resolve immediately if connected; otherwise wait for the next "open" up to timeoutMs, then
  // reject with an actionable message. This lets a request ride out a domain-reload bounce instead
  // of failing outright, while still giving up if the Editor is genuinely down.
  private waitForConnection(timeoutMs: number): Promise<void> {
    if (this.isConnected()) return Promise.resolve();
    if (this.shuttingDown) {
      return Promise.reject(new Error("MCP server shutting down."));
    }
    return new Promise<void>((resolve, reject) => {
      const waiter = {
        resolve,
        reject,
        timer: setTimeout(() => {
          const i = this.connectionWaiters.indexOf(waiter);
          if (i >= 0) this.connectionWaiters.splice(i, 1);
          reject(
            new Error(
              `Unity is not connected after ${Math.round(timeoutMs / 1000)}s. The Editor isn't ` +
                "running, or it has been compiling/reloading for a while - start or focus the " +
                "Unity Editor and retry.",
            ),
          );
        }, timeoutMs),
      };
      this.connectionWaiters.push(waiter);
    });
  }

  private resolveConnectionWaiters(): void {
    const waiters = this.connectionWaiters;
    this.connectionWaiters = [];
    for (const w of waiters) {
      clearTimeout(w.timer);
      w.resolve();
    }
  }

  // Public API

  public isConnected(): boolean {
    return (
      this.connected && this.ws !== null && this.ws.readyState === WebSocket.OPEN
    );
  }

  public getLogBuffer(): LogEntry[] {
    return [...this.logBuffer];
  }

  // Drop every buffered log and report how many were cleared. Only touches this server's buffer;
  // Unity keeps broadcasting new logs, so the buffer simply refills from "now" onward. Lets a
  // caller reset stale errors (e.g. a one-off failed compile) so later get_logs reads aren't
  // ambiguous about whether an error is current.
  public clearLogBuffer(): number {
    const cleared = this.logBuffer.length;
    this.logBuffer = [];
    return cleared;
  }

  // Send a request to Unity and resolve with the data from its correlated response.
  public async sendRequest(
    type: string,
    data: any,
    timeoutMs: number = 60_000,
  ): Promise<any> {
    // If Unity is mid-reload the socket is briefly gone; wait for it to come back (bounded) so a
    // recompile is a pause, not a failure. The request hasn't been sent yet, so waiting then
    // sending once is safe - unlike an in-flight drop, which the "close" handler surfaces instead
    // of silently resending. Throws here if the Editor stays down past connectWaitMs.
    await this.waitForConnection(this.connectWaitMs);

    return new Promise((resolve, reject) => {
      if (!this.isConnected()) {
        // Raced a drop between the wait resolving and here (rare). Still never sent, so it's safe
        // to retry.
        reject(
          new Error(
            "Unity is not connected (recompiling/reloading, or the Editor isn't running). " +
              "Retry in a moment.",
          ),
        );
        return;
      }

      const id = String(this.nextId++);
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(
          new Error(
            `Request "${type}" timed out after ${
              timeoutMs / 1000
            } seconds. The Unity Editor may be unfocused, compiling, or busy.`,
          ),
        );
      }, timeoutMs);

      this.pending.set(id, { resolve, reject, timer });

      try {
        this.ws!.send(JSON.stringify({ type, id, data }));
      } catch (error) {
        clearTimeout(timer);
        this.pending.delete(id);
        reject(error);
      }
    });
  }

  public close(): void {
    this.shuttingDown = true;
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    this.rejectPending(new Error("MCP server shutting down."));
    for (const w of this.connectionWaiters) {
      clearTimeout(w.timer);
      w.reject(new Error("MCP server shutting down."));
    }
    this.connectionWaiters = [];
    if (this.ws) {
      try {
        this.ws.close();
      } catch {
        // ignore
      }
      this.ws = null;
    }
  }
}
