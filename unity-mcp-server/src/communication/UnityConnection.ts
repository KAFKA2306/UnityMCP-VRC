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

  private logBuffer: LogEntry[] = [];
  private readonly maxLogBufferSize = 1000;

  // In-flight requests keyed by id, resolved when Unity sends the matching response.
  private pending = new Map<string, PendingRequest>();
  private nextId = 1;

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
      console.error(`[Unity MCP] Connected to Unity Editor at ${this.url}`);
      if (this.reconnectTimer) {
        clearTimeout(this.reconnectTimer);
        this.reconnectTimer = null;
      }
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
      // Fail any in-flight requests fast with a retry hint instead of letting them hang.
      this.rejectPending(
        new Error(
          "Unity disconnected (likely recompiling or reloading its app domain, or the " +
            "Editor is not running). The connection re-establishes automatically - retry in a moment.",
        ),
      );
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

      case "commandResult":
      case "editorState":
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

  // Public API

  public isConnected(): boolean {
    return (
      this.connected && this.ws !== null && this.ws.readyState === WebSocket.OPEN
    );
  }

  public getLogBuffer(): LogEntry[] {
    return [...this.logBuffer];
  }

  // Send a request to Unity and resolve with the data from its correlated response.
  public sendRequest(
    type: string,
    data: any,
    timeoutMs: number = 60_000,
  ): Promise<any> {
    return new Promise((resolve, reject) => {
      if (!this.isConnected()) {
        reject(new Error("Unity Editor is not connected."));
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
