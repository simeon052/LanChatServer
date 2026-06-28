# LanChatServer

同一LAN内のスマホ・タブレットから **Claude** および **Hermes Agent** とチャットできる、自己ホスト型HTTPサーバーです。

## 特徴

- **Claude** (Anthropic API) によるストリーミングチャット、会話履歴の永続化
- **Hermes Agent** (CLI) をサブプロセスとして起動し、stdin/stdout 経由でプロキシ
- **Claude Code セッションのインポート** — 過去の Claude Code 会話をスマホから継続可能
- モバイルファーストなダークテーマ Web UI (ブラウザのみで動作、アプリ不要)
- LAN 全体へバインド (`0.0.0.0`) し、起動時にアクセスURLをコンソール表示

## 必要環境

- .NET 10 SDK
- Anthropic API キー (Claude バックエンドを使う場合)
- `hermes` コマンドが PATH に存在すること (Hermes バックエンドを使う場合)

## クイックスタート

```bash
# 1. APIキーを設定
#    src/LanChatServer/appsettings.json を開き Claude.ApiKey を設定

# 2. 起動
cd src/LanChatServer
dotnet run

# 3. アクセス
#    PC:     http://localhost:5050
#    スマホ: コンソールに表示される http://<IP>:5050 を開く
```

## プロジェクト構成

```
LanChatServer/
├── src/LanChatServer/
│   ├── Config/AppConfig.cs          # 設定クラス
│   ├── Models/ChatModels.cs         # DTOモデル
│   ├── Services/
│   │   ├── AnthropicClient.cs       # Anthropic API SSEクライアント
│   │   ├── ClaudeService.cs         # Claudeセッション管理・永続化
│   │   ├── HermesService.cs         # Hermesプロセス管理
│   │   └── ClaudeCodeImporter.cs    # Claude Code JSONLパーサー
│   ├── Program.cs                   # エンドポイント定義・起動
│   ├── appsettings.json             # 設定ファイル
│   └── wwwroot/index.html           # Web UI
└── LanChatServer.slnx
```

## REST API

| メソッド | パス | 説明 |
|---|---|---|
| GET | `/healthz` | ヘルスチェック |
| GET | `/api/sessions` | セッション一覧 |
| POST | `/api/sessions` | セッション作成 |
| GET | `/api/sessions/{id}` | セッション詳細・履歴 |
| DELETE | `/api/sessions/{id}` | セッション削除 |
| POST | `/api/sessions/{id}/messages` | メッセージ送信 (SSE) |
| GET | `/api/claude-code/sessions` | Claude Code セッション一覧 |
| POST | `/api/claude-code/sessions/{id}/import` | Claude Code セッションをインポート |

## ライセンス

MIT
