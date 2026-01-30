# Project Overview

This repository is a fork and significant improvement of `Arodoid/UnityMCP`, designed to facilitate the integration of AI (specifically Claude) with the Unity Editor for VRChat world creation and general Unity development. Its primary goal is to enable AI to write and execute UdonSharp (C# for VRChat) code more effectively and accurately within Unity.

The project consists of two main components:

1.  **`unity-mcp-server/` (Node.js/TypeScript MCP Server):**
    *   This is an MCP (Model Context Protocol) server that acts as a bridge between an AI client (like Claude Desktop) and the Unity Editor.
    *   It communicates with the Unity Editor via WebSockets.
    *   It exposes various "tools" (e.g., `execute_editor_command`, `get_editor_state`) and "resources" to the AI, allowing the AI to interact with and retrieve information from the Unity Editor.
    *   It includes robust error handling, retry mechanisms for Unity connectivity, and log buffering.

2.  **`UnityMCPPlugin/` (Unity C# Plugin):**
    *   This is a Unity Editor plugin written in C# that resides within a Unity project's `Assets` folder.
    *   It establishes a WebSocket connection to the `unity-mcp-server`.
    *   It handles incoming commands from the server, executing C# code dynamically within the Unity Editor context, reporting editor state, and sending Unity console logs back to the server.
    *   It provides a manual "Script Tester" UI for developers to test C# scripts directly within Unity without involving the AI server.
    *   It includes VRChat-specific utilities, such as a helper for creating UdonSharp program assets from C# scripts.

## Main Technologies

*   **TypeScript/Node.js:** For the MCP server.
*   **C#:** For the Unity Editor plugin.
*   **Unity Editor API:** Extensive use within the C# plugin for editor manipulation.
*   **WebSockets:** For communication between the server and the Unity plugin.
*   **Model Context Protocol (MCP):** The framework for AI interaction.
*   **UdonSharp:** VRChat's custom C# scripting language, targeted by the AI.
*   **Newtonsoft.Json (Json.NET):** Used in the C# plugin for JSON serialization/deserialization.

## Architecture Highlights

*   **Client-Server Model:** The Node.js server acts as the central hub, mediating between the AI and Unity.
*   **Dynamic C# Execution:** The Unity plugin can compile and execute arbitrary C# code provided by the AI in memory, referencing a comprehensive set of Unity, .NET, and VRChat assemblies.
*   **Unified Logging:** Unity console logs are captured and streamed back to the server, providing the AI with real-time feedback.
*   **Error Handling:** Both server and client components include mechanisms for error reporting, connection retries, and log truncation (for error stack traces on the Unity side) to optimize for AI context windows.
*   **Extensible Tooling:** The server's `src/tools/` and `src/resources/` directories are designed to be easily extensible with new AI-callable functions and data sources.

# Building and Running

## `unity-mcp-server`

Navigate to the `unity-mcp-server/` directory.

1.  **Install Dependencies:**
    ```bash
    npm install
    ```
2.  **Build the Server:**
    ```bash
    npm run build
    ```
    This command compiles TypeScript, copies resources, and makes the main `build/index.js` executable.
3.  **Run the Server:**
    The server is typically started by an MCP client (e.g., Claude Desktop) configured to run `build/index.js`.
    Manually, you could run:
    ```bash
    node build/index.js
    ```
    Or, if configured as a `bin` in `package.json`, it might be run via `npx` or by directly executing `build/index.js` if it's in your PATH.

## `UnityMCPPlugin`

1.  **Copy to Unity Project:**
    Copy the entire `UnityMCPPlugin/` folder into the `Assets` directory of your Unity project.
2.  **Install Newtonsoft.Json:**
    Ensure `com.unity.nuget.newtonsoft-json` (version `3.0.2` or compatible) is installed in your Unity project, typically via the Package Manager.
3.  **Open Debug Window:**
    In the Unity Editor, open the `UnityMCP` menu and select `Debug Window`. This window shows the connection status to the MCP server.
4.  **Configure MCP Client (e.g., Claude Desktop):**
    Set up your MCP client to connect to the server, pointing to the built `unity-mcp-server/build/index.js` file.
    Example configuration (from `README.md`):
    ```json
    {
        "mcpServers": {
            "unity": {
                "command": "node",
                "args": [
                    "C:\\git\\UnityMCP\\unity-mcp-server\\build\\index.js"
                ]
            }
        }
    }
    ```

# Development Conventions

*   **Code Structure:** The project follows a clear separation of concerns, with Node.js server logic in `unity-mcp-server/src` and Unity plugin logic in `UnityMCPPlugin/Editor`.
*   **TypeScript Best Practices:** The Node.js server uses `strict` TypeScript settings.
*   **MCP Integration:** Strict adherence to the Model Context Protocol for AI communication.
*   **Dynamic C# Compilation:** C# code intended for execution by the AI must adhere to the `EditorCommand` class and `static object Execute()` method signature.
*   **Logging:** Both client and server components provide detailed logging, with Unity-side error stack traces being truncated to the first line when sent back to the server to optimize context.
*   **No explicit build tool like Taskfile currently identified for the top-level project.** Individual components use `npm` for scripts.
