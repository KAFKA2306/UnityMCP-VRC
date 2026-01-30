import { ErrorCode, McpError } from "@modelcontextprotocol/sdk/types.js";
import {
  Tool,
  ToolContext,
  ToolDefinition,
} from "./types.js";

export interface CommandResult {
  result: any;
  logs: string[];
  errors: string[];
  warnings: string[];
  executionSuccess: boolean;
  errorDetails?: {
    message: string;
    stackTrace: string;
    type: string;
  };
}

export interface CommandResultHandler {
  resolve: (value: CommandResult) => void;
  reject: (reason?: any) => void;
}

let commandResultPromise: CommandResultHandler | null = null;
let commandStartTime: number | null = null;

export function resolveCommandResult(result: CommandResult): void {
  if (commandResultPromise) {
    commandResultPromise.resolve(result);
    commandResultPromise = null;
  }
}

export class ExecuteEditorCommandTool implements Tool {
  getDefinition(): ToolDefinition {
    return {
      name: "execute_editor_command",
      description:
        "Execute arbitrary C# code file within the Unity Editor context. This powerful tool allows for direct manipulation of the Unity Editor, GameObjects, components, and project assets using the Unity Editor API.",
      category: "Editor Control",
      tags: ["unity", "editor", "command", "c#", "scripting"],
      inputSchema: {
        type: "object",
        properties: {
          code: {
            type: "string",
            description: `C# code file to execute in the Unity Editor context. 
The code has access to all UnityEditor and UnityEngine APIs. 
Include any necessary using directives at the top of the code.
The code must have a EditorCommand class with a static Execute method that returns an object.`,
            minLength: 1,
          },
        },
        required: ["code"],
        additionalProperties: false,
      },
      returns: {
        type: "object",
        description:
          "Returns the execution result and any logs generated during execution",
        format: 'JSON object containing "result" and "logs" fields',
      },
      errorHandling: {
        description: "Common error scenarios and their handling:",
        scenarios: [
          {
            error: "Compilation error",
            handling: "Returns compilation error details in logs",
          },
          {
            error: "Runtime exception",
            handling: "Returns exception details and stack trace",
          },
          {
            error: "Timeout",
            handling: "Command execution timeout after 5 seconds",
          },
        ],
      },
      examples: [
        {
          description: "Center selected object",
          input: {
            code: `using UnityEngine;
using UnityEditor;
using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

public class EditorCommand
{
  public static object Execute()
  {
      Selection.activeGameObject.transform.position = Vector3.zero;
      EditorApplication.isPlaying = !EditorApplication.isPlaying;
      return ""Success"";
  }
}`,
          },
          output:
            '{ "result": true, "logs": ["[UnityMCP] Command executed successfully"] }',
        },
      ],
    };
  }

  async execute(args: any, context: ToolContext) {
    if (!args?.code) {
      throw new McpError(
        ErrorCode.InvalidParams,
        "The code parameter is required",
      );
    }

    if (typeof args.code !== "string") {
      throw new McpError(
        ErrorCode.InvalidParams,
        "The code parameter must be a string",
      );
    }

    if (args.code.trim().length === 0) {
      throw new McpError(
        ErrorCode.InvalidParams,
        "The code parameter cannot be empty",
      );
    }

    try {
      const startLogIndex = context.logBuffer.length;
      commandStartTime = Date.now();

      context.unityConnection!.sendMessage("executeEditorCommand", {
        code: args.code,
      });

      const result = await new Promise((resolve, reject) => {
        commandResultPromise = { resolve, reject };
      });

      const commandLogs = context.logBuffer
        .slice(startLogIndex)
        .filter((log) => log.message.includes("[UnityMCP]"));

      const executionTime = Date.now() - (commandStartTime || 0);

      return {
        content: [
          {
            type: "text",
            text: JSON.stringify(
              {
                result,
                logs: commandLogs,
                executionTime: `${executionTime}ms`,
                status: "success",
              },
              null,
              2,
            ),
          },
        ],
      };
    } catch (error) {
      if (error instanceof Error) {
        if (error.message.includes("timed out")) {
          throw new McpError(ErrorCode.InternalError, error.message);
        }

        if (error.message.includes("NullReferenceException")) {
          throw new McpError(
            ErrorCode.InvalidParams,
            "The code attempted to access a null object. Please check that all GameObject references exist.",
          );
        }

        if (error.message.includes("CompileError")) {
          throw new McpError(
            ErrorCode.InvalidParams,
            "C# compilation error. Please check the syntax of your code.",
          );
        }
      }

      throw new McpError(
        ErrorCode.InternalError,
        `Failed to execute command: ${
          error instanceof Error ? error.message : "Unknown error"
        }`,
      );
    }
  }
}