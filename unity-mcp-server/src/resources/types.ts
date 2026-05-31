import { UnityConnection } from "../communication/UnityConnection.js";

export interface ResourceDefinition {
  uri: string;
  name: string;
  mimeType: string;
  description?: string;
}

export interface ResourceContext {
  unityConnection: UnityConnection;
}

// A readable MCP resource (exposed as file:///<name>) the client can pull in for extra context.
export interface Resource {
  getDefinition(): ResourceDefinition;
  getContents(context: ResourceContext): Promise<string>;
}
