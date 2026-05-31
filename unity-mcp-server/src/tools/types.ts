import { UnityConnection } from "../communication/UnityConnection.js";

export interface LogEntry {
  message: string;
  stackTrace: string;
  logType: string;
  timestamp: string;
}

// The shape every tool exposes: getDefinition() is advertised to the MCP client (tools/list) and
// execute() runs the call. Tools live one-per-file behind this interface (registered in index.ts).
export interface ToolDefinition {
  name: string; // MCP tool id (snake_case), e.g. "execute_editor_command"
  description: string;
  category: string; // free-form grouping label for humans; not used for dispatch
  tags: string[];
  inputSchema: object; // JSON Schema for the arguments, validated by the MCP client
  returns: object; // documents the result shape; informational only, not enforced
  examples: {
    description: string;
    input: any;
    output: string;
  }[];
  errorHandling?: {
    description: string;
    scenarios: {
      error: string;
      handling: string;
    }[];
  };
}

// What a tool's execute() receives: the live Unity connection (logs are fetched through it now).
export interface ToolContext {
  unityConnection: UnityConnection;
}

export interface Tool {
  getDefinition(): ToolDefinition;
  execute(args: any, context: ToolContext): Promise<any>;
}
