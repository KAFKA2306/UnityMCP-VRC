// The Unity Editor hosts a tiny HTTP endpoint; this MCP server sends one request per tool call.
//
// This used to be a persistent WebSocket client, but keeping a long-lived socket alive across
// Unity's frequent domain reloads is what generated nearly all the connection-handling complexity
// (a reconnect loop, an id-keyed pending map, ride-out-reload waits, and a plugin-side teardown that
// kept stalling the reload). Stateless request/response collapses all of that into "POST a command,
// read the response" plus a bounded retry when the Editor is briefly unreachable (mid-reload or not
// started yet). Unity still owns the one port, so any number of MCP servers share one Editor.
export class UnityConnection {
  private readonly baseUrl: string;
  private shuttingDown = false;

  // How long to keep retrying a refused connection before giving up. Covers a domain-reload bounce
  // (the listener is down for a beat) or the Editor still starting; past it we assume it's down.
  private readonly connectWaitMs = 20_000;

  constructor(port: number = 8080) {
    this.baseUrl = `http://localhost:${port}/`;
  }

  // Send a tool request to Unity and resolve with its JSON response payload. POSTs { type, data };
  // the Editor runs the work on its main thread and returns the result as the response body.
  //
  // Failure handling mirrors what the old WebSocket layer did, inferred from the HTTP outcome:
  // - connection *refused* (Editor down or mid-reload, request never arrived) -> retry up to
  //   connectWaitMs, so a reload is a brief pause rather than a failure;
  // - connection *reset* mid-request (Editor likely started reloading after we sent) -> NOT retried,
  //   surfaced as "may have applied" since a non-idempotent command might have run;
  // - timeout -> surfaced with the usual unfocused/busy hint.
  public async sendRequest(
    type: string,
    data: any,
    timeoutMs: number = 60_000,
  ): Promise<any> {
    if (this.shuttingDown) throw new Error("MCP server shutting down.");

    const deadline = Date.now() + this.connectWaitMs;
    let attempt = 0;

    while (true) {
      attempt++;
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), timeoutMs);
      try {
        const res = await fetch(this.baseUrl, {
          method: "POST",
          headers: { "content-type": "application/json" },
          body: JSON.stringify({ type, data }),
          signal: controller.signal,
        });

        if (!res.ok) {
          const text = await res.text().catch(() => "");
          throw new Error(
            `Unity returned ${res.status} ${res.statusText}${text ? `: ${text}` : ""}`,
          );
        }

        return await res.json();
      } catch (err: any) {
        // Refused = the request never reached Unity, so it's safe to retry while we wait out a
        // reload/startup. Anything else (timeout, reset, HTTP error) is surfaced as-is.
        if (this.isConnRefused(err) && !this.shuttingDown && Date.now() < deadline) {
          await delay(Math.min(500 * attempt, 2000));
          continue;
        }
        throw this.describe(err, timeoutMs);
      } finally {
        clearTimeout(timer);
      }
    }
  }

  public close(): void {
    this.shuttingDown = true;
  }

  private isConnRefused(err: any): boolean {
    return (err?.cause?.code ?? err?.code) === "ECONNREFUSED";
  }

  // Turn a fetch failure into an actionable message, preserving the substrings the tools key off
  // ("timed out", and the reset -> "may have applied" wording).
  private describe(err: any, timeoutMs: number): Error {
    if (err?.name === "AbortError") {
      return new Error(
        `Request timed out after ${Math.round(timeoutMs / 1000)}s. The Unity Editor may be ` +
          "unfocused, compiling, or busy - focus it (or set Interaction Mode to No Throttling) and retry.",
      );
    }
    const code = err?.cause?.code ?? err?.code;
    if (code === "ECONNREFUSED") {
      return new Error(
        "Unity isn't reachable on http://localhost:8080 - is the Editor running with the UnityMCP " +
          "plugin? (It may also be mid domain-reload; it should come back within a few seconds.)",
      );
    }
    if (code === "ECONNRESET" || code === "UND_ERR_SOCKET") {
      return new Error(
        "Unity dropped the connection mid-request (it likely began a domain reload). The command " +
          "may or may not have applied - check the editor/scene state before retrying.",
      );
    }
    return err instanceof Error ? err : new Error(String(err));
  }
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
