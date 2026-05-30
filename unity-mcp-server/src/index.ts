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

/**
 * True for errors that mean the Unity link dropped (typically a domain reload from a recompile),
 * as opposed to a genuine command failure or a request timeout. Used to decide whether a mid-call
 * failure is worth transparently retrying. Matches on message text because the error crosses the
 * WebSocket/tool boundary as a plain Error/McpError. Deliberately excludes "timed out" so a real
 * timeout isn't retried into a multi-minute hang.
 */
function isConnectionLost(error: unknown): boolean {
  const message = error instanceof Error ? error.message : String(error ?? "");
  return /not connected|disconnected|recompil|reload/i.test(message);
}

class UnityMCPServer {
  private server: Server;
  private unityConnection: UnityConnection;
  private initialized = false;

  constructor() {
    // Initialize MCP Server
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

    // Error handling
    this.server.onerror = (error) => console.error("[MCP Error]", error);
    process.on("SIGINT", async () => {
      await this.cleanup();
      process.exit(0);
    });
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
          // Add any other context properties needed
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

    // List available tools with comprehensive documentation
    this.server.setRequestHandler(ListToolsRequestSchema, async () => ({
      tools: tools.map((tool) => tool.getDefinition()),
    }));

    // Handle tool calls with enhanced validation and error handling
    this.server.setRequestHandler(CallToolRequestSchema, async (request) => {
      const { name, arguments: args } = request.params;

      // Find the requested tool
      const tool = tools.find((t) => t.getDefinition().name === name);

      // Validate tool exists with helpful error message
      if (!tool) {
        const availableTools = tools.map((t) => t.getDefinition().name);
        throw new McpError(
          ErrorCode.MethodNotFound,
          `Unknown tool: ${name}. Available tools are: ${availableTools.join(
            ", ",
          )}`,
        );
      }

      // Tools that only read server-side state (e.g. get_command_page reads the cached
      // command result) don't need Unity, so skip the connection gate for them.
      const requiresUnity = tool.getDefinition().requiresUnity !== false;

      // Try executing with retry logic for connection issues
      let retryCount = 0;
      const maxRetries = 5;
      const retryDelay = 5000; // 5 seconds

      while (true) {
        // Verify Unity connection with detailed error message
        if (requiresUnity && !this.unityConnection.isConnected()) {
          if (retryCount < maxRetries) {
            retryCount++;
            console.error(`Unity Editor not connected (likely recompiling/reloading). Retrying in 5 seconds... (${retryCount}/${maxRetries})`);
            await new Promise(resolve => setTimeout(resolve, retryDelay));
            continue;
          }
          throw new McpError(
            ErrorCode.InternalError,
            "Unity Editor is not connected. Please ensure the Unity Editor is running and the UnityMCP window is open.",
          );
        }

        // Create context object for tool execution
        const toolContext: ToolContext = {
          unityConnection: this.unityConnection,
          logBuffer: this.unityConnection.getLogBuffer(),
        };

        // Execute the tool
        try {
          return await tool.execute(args, toolContext);
        } catch (error) {
          // A call can race a domain reload: the gate above saw a live socket, but Unity tore the
          // connection down mid-flight (recompile/reload triggered by an earlier edit). Transparently
          // retry the connection-loss case - same budget as the gate - so a reload is a brief blip
          // rather than a surfaced error. Real command failures and timeouts propagate immediately.
          if (
            requiresUnity &&
            retryCount < maxRetries &&
            isConnectionLost(error)
          ) {
            retryCount++;
            console.error(`Unity connection lost mid-call (likely recompiling/reloading). Retrying in 5 seconds... (${retryCount}/${maxRetries})`);
            await new Promise(resolve => setTimeout(resolve, retryDelay));
            continue;
          }
          throw error;
        }
      }
    });
  }

  private async cleanup() {
    this.unityConnection.close();
    await this.server.close();
  }

  async run() {
    await this.initialize();
    
    const transport = new StdioServerTransport();
    await this.server.connect(transport);
    console.error("Unity MCP server running on stdio");

    // Brief settle before run() returns. The Unity client connects in the background
    // and reconnects on its own, so there's nothing to block on here.
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 100);
    });
  }
}

const server = new UnityMCPServer();
server.run().catch(console.error);
