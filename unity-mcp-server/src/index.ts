#!/usr/bin/env node
import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  CallToolRequestSchema,
  ErrorCode,
  ListResourcesRequestSchema,
  ListToolsRequestSchema,
  McpError,
  ReadResourceRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import { UnityConnection } from "./communication/UnityConnection.js";
import { getAllResources, ResourceContext } from "./resources/index.js";
import { getAllTools, ToolContext } from "./tools/index.js";

class UnityMCPServer {
  private server: Server;
  private unityConnection: UnityConnection;
  private initialized = false;
  private shuttingDown = false;

  constructor() {
    this.server = new Server(
      {
        name: "unity-mcp-server",
        version: "0.1.0",
      },
      {
        capabilities: {
          tools: {},
          resources: {},
        },
      },
    );

    // Connect (as a client) to the WebSocket server the Unity plugin hosts.
    this.unityConnection = new UnityConnection(8080);

    // Log MCP-layer errors, and exit cleanly when interrupted or asked to terminate.
    this.server.onerror = (error) => console.error("[MCP Error]", error);
    process.on("SIGINT", () => this.shutdown(0));
    process.on("SIGTERM", () => this.shutdown(0));
  }

  /** Initialize the server asynchronously */
  async initialize() {
    if (this.initialized) return;
    
    await this.setupResources();
    this.setupTools();
    
    this.initialized = true;
  }

  /** Optional resources the user can include in Claude Desktop to give additional context to the LLM */
  private async setupResources() {
    const resources = await getAllResources();

    // Set up the resource request handler
    this.server.setRequestHandler(
      ListResourcesRequestSchema,
      async (request) => {
        return {
          resources: resources.map((resource) => resource.getDefinition()),
        };
      },
    );

    // Read resource contents
    this.server.setRequestHandler(
      ReadResourceRequestSchema,
      async (request) => {
        const uri = request.params.uri;
        const resource = resources.find((r) => r.getDefinition().uri === uri);

        if (!resource) {
          throw new McpError(
            ErrorCode.MethodNotFound,
            `Resource not found: ${uri}. Available resources: ${resources
              .map((r) => r.getDefinition().uri)
              .join(", ")}`,
          );
        }

        const resourceContext: ResourceContext = {
          unityConnection: this.unityConnection,
        };

        const content = await resource.getContents(resourceContext);

        return {
          contents: [
            {
              uri,
              mimeType: resource.getDefinition().mimeType,
              text: content,
            },
          ],
        };
      },
    );
  }

  private setupTools() {
    const tools = getAllTools();

    // Advertise the available tools and their schemas.
    this.server.setRequestHandler(ListToolsRequestSchema, async () => ({
      tools: tools.map((tool) => tool.getDefinition()),
    }));

    // Dispatch a tool call to the matching tool.
    this.server.setRequestHandler(CallToolRequestSchema, async (request) => {
      const { name, arguments: args } = request.params;

      const tool = tools.find((t) => t.getDefinition().name === name);

      // Unknown tool name: fail with the list of valid ones.
      if (!tool) {
        const availableTools = tools.map((t) => t.getDefinition().name);
        throw new McpError(
          ErrorCode.MethodNotFound,
          `Unknown tool: ${name}. Available tools are: ${availableTools.join(
            ", ",
          )}`,
        );
      }

      // No connection gate here. Tools that talk to Unity go through UnityConnection.sendRequest,
      // which waits (bounded) for a live socket before sending - so a request issued during a
      // domain reload pauses for the reconnect instead of failing, and a genuinely-down Editor
      // surfaces a clear message. Cache/buffer-only tools (get_command_page, get_logs) don't call
      // sendRequest and so run regardless of connection state.
      const toolContext: ToolContext = {
        unityConnection: this.unityConnection,
        logBuffer: this.unityConnection.getLogBuffer(),
      };

      return await tool.execute(args, toolContext);
    });
  }

  private async cleanup() {
    this.unityConnection.close();
    await this.server.close();
  }

  /**
   * Tear down and exit. The client owns our lifetime, so when it disconnects we must exit rather
   * than linger: UnityConnection's 3s reconnect timer keeps the event loop alive indefinitely,
   * which previously left zombie servers endlessly reconnecting to the Editor and storming it on
   * every domain reload. Idempotent, and force-exits if a graceful close stalls.
   */
  private shutdown(code = 0): void {
    if (this.shuttingDown) return;
    this.shuttingDown = true;
    const force = setTimeout(() => process.exit(code), 2000);
    force.unref();
    this.cleanup()
      .catch(() => {})
      .then(() => process.exit(code));
  }

  async run() {
    await this.initialize();

    const transport = new StdioServerTransport();
    await this.server.connect(transport);
    console.error("Unity MCP server running on stdio");

    // The client owns our lifetime: when it goes away, its end of our stdio closes. Exit then, so
    // we don't linger reconnecting to Unity. server.onclose fires on transport close; the stdin
    // listeners are a backstop in case the transport doesn't surface the EOF itself.
    this.server.onclose = () => this.shutdown(0);
    process.stdin.on("end", () => this.shutdown(0));
    process.stdin.on("close", () => this.shutdown(0));

    // Brief settle before run() returns. The Unity client connects in the background
    // and reconnects on its own, so there's nothing to block on here.
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 100);
    });
  }
}

const server = new UnityMCPServer();
server.run().catch(console.error);
