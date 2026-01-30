# Unity MCP Server

Unity MCP Server は、Model Context Protocol (MCP) を実装した Node.js アプリケーションです。Claude Desktop などの MCP クライアントと Unity Editor の間のブリッジとして機能します。

## アーキテクチャ

このサーバーは WebSocket を使用して Unity Editor と通信し、標準入出力 (stdio) を使用して MCP クライアントと通信します。

```mermaid
graph LR
    Client[MCP Client\n(Claude Desktop)] -- stdio --> Server[MCP Server\n(Node.js)]
    Server -- WebSocket (Port 8080) --> Unity[Unity Editor\n(Plugin)]
```

## 主な機能

1.  **MCP サーバー**: `src/index.ts` がエントリーポイントとなり、MCP プロトコル（ツール、リソース）を処理します。
2.  **WebSocket サーバー**: Unity プラグインからの接続を待ち受けます。
3.  **ツール提供**: `execute_editor_command` などのツールをクライアントに公開します。
4.  **リソース提供**: `resources/text` 以下のファイルを読み込み、コンテキストとして提供します。

## セットアップ

```bash
npm install
npm run build
```

ビルド成果物は `build/` ディレクトリに出力されます。

