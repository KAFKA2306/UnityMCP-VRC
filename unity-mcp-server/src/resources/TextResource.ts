import fs from "fs/promises";
import path from "path";
import { Resource, ResourceContext, ResourceDefinition } from "./types.js";

export class TextResource implements Resource {
  private filePath: string;
  private fileName: string;

  constructor(filePath: string) {
    this.filePath = filePath;
    this.fileName = path.basename(filePath);
  }

  getDefinition(): ResourceDefinition {
    return {
      uri: `file:///${this.fileName}`,
      name: this.fileName,
      mimeType: "text/plain",
      description: `Text file: ${this.fileName}`,
    };
  }

  async getContents(context: ResourceContext): Promise<string> {
    return await fs.readFile(this.filePath, "utf8");
  }
}

export async function loadTextResources(
  directoryPath: string,
): Promise<TextResource[]> {
  const textFiles = await fs.readdir(directoryPath);

  return textFiles.map(
    (file) => new TextResource(path.join(directoryPath, file)),
  );
}
