import { ErrorCode, McpError } from "@modelcontextprotocol/sdk/types.js";
import { Tool, ToolContext, ToolDefinition } from "./types.js";

export interface UnityEditorState {
  activeGameObjects: string[];
  selectedObjects: string[];
  playModeState: string;
  sceneHierarchy: any;
  projectStructure: {
    scenes?: string[];
    assets?: string[];
    [key: string]: string[] | undefined;
  };
}

export interface UnityEditorStateHandler {
  resolve: (value: UnityEditorState) => void;
  reject: (reason?: any) => void;
}

let unityEditorStatePromise: UnityEditorStateHandler | null = null;
let unityEditorStateTime: number | null = null;

export function resolveUnityEditorState(result: UnityEditorState): void {
  if (unityEditorStatePromise) {
    unityEditorStatePromise.resolve(result);
    unityEditorStatePromise = null;
  }
}

export class GetEditorStateTool implements Tool {
  getDefinition(): ToolDefinition {
    return {
      name: "get_editor_state",
      description:
        "Retrieve the current state of the Unity Editor, including active GameObjects, selection state, play mode status, scene hierarchy, project structure, and assets. This tool provides a comprehensive snapshot of the editor's current context.",
      category: "Editor State",
      tags: ["unity", "editor", "state", "hierarchy", "project"],
      inputSchema: {
        type: "object",
        properties: {
          format: {
            type: "string",
            enum: ["Raw"],
            description:
              "Specify the output format:\n- Raw: Complete editor state including all available data",
            default: "Raw",
          },
        },
        additionalProperties: false,
      },
      returns: {
        type: "object",
        description:
          "Returns a JSON object containing the requested editor state information",
        format:
          "The response format varies based on the format parameter:\n- Raw: Full UnityEditorState object",
      },
      examples: [
        {
          description: "Get complete editor state",
          input: { format: "Raw" },
          output:
            '{ "activeGameObjects": ["Main Camera", "Directional Light"], ... }',
        },
      ],
    };
  }

  async execute(args: any, context: ToolContext) {
    const validFormats = ["Raw"];
    const format = (args?.format as string) || "Raw";

    if (args?.format && !validFormats.includes(format)) {
      throw new McpError(
        ErrorCode.InvalidParams,
        `Invalid format: "${format}". Valid formats are: ${validFormats.join(
          ", ",
        )}`,
      );
    }

    try {
      const startLogIndex = context.logBuffer.length;
      unityEditorStateTime = Date.now();

      context.unityConnection!.sendMessage("getEditorState", {});

      const editorState = await new Promise<UnityEditorState>((resolve, reject) => {
        unityEditorStatePromise = { resolve, reject };
      });

      let responseData: any;
      switch (format) {
        case "Raw":
          responseData = editorState;
          break;
      }

      return {
        content: [
          {
            type: "text",
            text: JSON.stringify(responseData, null, 2),
          },
        ],
      };
    } catch (error) {
      if (error instanceof Error && error.message.includes("timed out")) {
        throw new McpError(ErrorCode.InternalError, error.message);
      }

      throw new McpError(
        ErrorCode.InternalError,
        `Failed to process editor state: ${
          error instanceof Error ? error.message : "Unknown error"
        }`,
      );
    }
  }
}
