# Editor Scripts

Unity Editor 内で動作する主要なスクリプト群です。

## クラス構成

| クラス | 役割 |
| :--- | :--- |
| `UnityMCPConnection` | `ClientWebSocket` を使用してサーバーに接続します。非同期ループでメッセージを受信します。 |
| `EditorCommandExecutor` | 受信した C# コードを動的にコンパイルして実行します。 |
| `UnityMCPWindow` | 接続状態を表示するエディタウィンドウを提供します。 |
| `ScriptTesterWindow` | 手動で C# スクリプトをテスト実行するためのデバッグ用ウィンドウです。 |

## 動的実行の仕組み (`EditorCommandExecutor`)

1.  **パース**: 受け取ったコードから `using` などを解析します。
2.  **ラップ**: 必要に応じてコードをクラス構造にラップします。
3.  **コンパイル**: `Microsoft.CSharp.CSharpCodeProvider` を使用してインメモリでコンパイルします。
4.  **実行**: リフレクションを使用して `Execute` メソッドを呼び出します。

```mermaid
graph TD
    Code[C# Code String] --> Compiler[CSharpCodeProvider]
    Compiler --> Assembly[Assembly (Memory)]
    Assembly --> Reflection[Reflection Invoke]
    Reflection --> UnityAPI[Unity Editor API]
```
