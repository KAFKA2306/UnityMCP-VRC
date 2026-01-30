#!/usr/bin/env node
import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  CallToolRequestSchema,
  ListResourcesRequestSchema,
  ListToolsRequestSchema,
  ReadResourceRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import { UnityConnection } from "./communication/UnityConnection.js";
import { getAllResources, ResourceContext } from "./resources/index.js";
import { getAllTools, ToolContext } from "./tools/index.js";

class UnityMCPServer {
  private server: Server;
  private unityConnection: UnityConnection;
  private initialized = false;

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

    this.unityConnection = new UnityConnection(8080);

    this.server.onerror = (error) => console.error("[MCP Error]", error);
    process.on("SIGINT", async () => {
      await this.cleanup();
      process.exit(0);
    });
  }

  async initialize() {
    if (this.initialized) return;
    
    await this.setupResources();
    this.setupTools();
    
    this.initialized = true;
  }

  private async setupResources() {
    const resources = await getAllResources();

    this.server.setRequestHandler(
      ListResourcesRequestSchema,
      async (request) => {
        return {
          resources: resources.map((resource) => resource.getDefinition()),
        };
      },
    );

    this.server.setRequestHandler(
      ReadResourceRequestSchema,
      async (request) => {
        const uri = request.params.uri;
        const resource = resources.find((r) => r.getDefinition().uri === uri);

        if (!resource) {
          throw new Error(
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

    this.server.setRequestHandler(ListToolsRequestSchema, async () => ({
      tools: tools.map((tool) => tool.getDefinition()),
    }));

    this.server.setRequestHandler(CallToolRequestSchema, async (request) => {
      const { name, arguments: args } = request.params;

      const tool = tools.find((t) => t.getDefinition().name === name);

      if (!tool) {
        const availableTools = tools.map((t) => t.getDefinition().name);
        throw new Error(
          `Unknown tool: ${name}. Available tools are: ${availableTools.join(
            ", ",
          )}`,
        );
      }

      if (!this.unityConnection.isConnected()) {
        throw new Error(
          "Unity Editor is not connected. Please ensure the Unity Editor is running and the UnityMCP window is open.",
        );
      }

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

  async run() {
    await this.initialize();
    
    const transport = new StdioServerTransport();
    await this.server.connect(transport);
    console.error("Unity MCP server running on stdio");
  }
}

const server = new UnityMCPServer();
server.run().catch(console.error);