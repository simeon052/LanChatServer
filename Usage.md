# 使い方ガイド

## 初回セットアップ

### 1. APIキーの設定

`src/LanChatServer/appsettings.json` を開き、`Claude.ApiKey` に Anthropic API キーを設定します。

```json
{
  "Claude": {
    "ApiKey": "sk-ant-xxxxxxxxxxxxxxxx",
    "DefaultModel": "claude-sonnet-4-6",
    "MaxTokens": 8192,
    "DefaultSystemPrompt": "You are a helpful assistant."
  }
}
```

APIキーは [Anthropic Console](https://console.anthropic.com/) から取得できます。

### 2. ポート変更 (任意)

デフォルトは `5050` ポートです。変更する場合:

```json
{
  "Server": {
    "Host": "0.0.0.0",
    "Port": 8080
  }
}
```

### 3. Hermes Agent の設定 (任意)

`hermes` コマンドがデフォルトPATHにない場合、フルパスを指定します。

```json
{
  "Hermes": {
    "Command": "C:\\Users\\yourname\\AppData\\Local\\hermes\\hermes.exe",
    "Arguments": "",
    "ResponseTimeoutMs": 1500
  }
}
```

`ResponseTimeoutMs` は、Hermesからの応答が途切れてから「返答完了」と判断するまでのミリ秒です。Hermesの応答が途中で切れる場合は値を増やしてください。

---

## 起動

```bash
cd src/LanChatServer
dotnet run
```

起動すると以下のようなコンソール出力が表示されます:

```
╔══════════════════════════════════════╗
║         LanChatServer 起動           ║
╚══════════════════════════════════════╝
  PC:     http://localhost:5050
  スマホ: http://192.168.10.xxx:5050
  Claude: ✓ claude-sonnet-4-6
  Hermes: hermes
  Ctrl+C で終了
```

**スマホ**で `http://192.168.10.xxx:5050` を開くと Web UI が表示されます。

---

## Web UI の使い方

### 新しいセッションの作成

1. サイドバー右上の **「+ 新規」** をタップ
2. バックエンドを選択:
   - **Claude** — Anthropic API に直接接続。モデルとシステムプロンプトを指定可能
   - **Hermes Agent** — ローカルの `hermes` プロセスを起動
3. **「作成」** をタップ

### メッセージの送信

- テキストエリアに入力し、**↑ ボタン**または **Enter** で送信
- **Shift+Enter** で改行
- Claudeバックエンドはトークンが届き次第リアルタイムで表示されます

### セッションの管理

- 左サイドバーにセッション一覧が表示されます
- セッション名にカーソルを合わせると表示される **✕** で削除できます
- スマホでは左上の **☰** メニューからサイドバーを開けます

---

## Claude Code セッションの再開

PC で Claude Code を使って作業した会話を、スマホから続けることができます。

### 手順

1. サイドバー下部の **「📥 Claude Codeから再開」** をタップ
2. `~/.claude/projects/` 内のセッション一覧が表示されます
3. 再開したいセッションをタップ → 自動でインポートされます
4. 通常のClaude チャットとして会話を続けられます

### 注意事項

| 引き継がれるもの | 引き継がれないもの |
|---|---|
| ユーザーのメッセージ | ツール呼び出し履歴 (ファイル読み書き等) |
| Claudeのテキスト返答 | thinking ブロック |
| 会話の文脈・前提 | コードの実行結果 |

コーディングセッションを再開する場合、Claudeは会話の文脈は持ちますが、実際のファイル内容は把握していません。必要に応じて「現在の `XXX.cs` の内容は〜」と補足すると精度が上がります。

---

## コンソールログの見方

```
[10:30:01] [INFO] Claude セッション作成 [abc123] model=claude-sonnet-4-6 (from 192.168.10.5)
[10:30:15] [INFO] Claude [abc123] ← "StockWatchのバグを直して" (from 192.168.10.5)
[10:30:28] [OK]   Claude [abc123] → 843文字 (13204ms)
[10:31:00] [INFO] Hermes セッション作成 (from 192.168.10.5)
[10:31:01] [OK]   Hermes セッション開始 [def456]
[10:31:10] [INFO] Hermes [def456] ← "今日の天気は？" (from 192.168.10.5)
[10:31:13] [OK]   Hermes [def456] → 256文字 (3041ms)
[10:32:00] [WARN] 空のセッション: 47cdcfde-...
[10:32:05] [ERROR] Hermes 起動失敗: hermes コマンドが見つかりません
```

| レベル | 意味 |
|---|---|
| `[INFO]` | 操作の開始・受信 |
| `[OK]` | 操作の正常完了 |
| `[WARN]` | 軽微な問題 (処理は継続) |
| `[ERROR]` | エラー (接続元には適切なHTTPエラーを返却) |

---

## トラブルシューティング

### スマホからアクセスできない

- PC と スマホが**同じWi-Fiネットワーク**に接続されているか確認
- Windows ファイアウォールでポート `5050` を許可してください:
  ```
  netsh advfirewall firewall add rule name="LanChatServer" dir=in action=allow protocol=TCP localport=5050
  ```

### Claude が「APIキー未設定」とエラーを返す

`appsettings.json` の `Claude.ApiKey` が `YOUR_ANTHROPIC_API_KEY` のままです。正しいキーに書き換えてから再起動してください。

### Hermes セッション作成が失敗する

- `hermes` コマンドが PATH に存在するか確認: `where hermes`
- `appsettings.json` の `Hermes.Command` にフルパスを指定してみてください

### Hermes の返答が途中で切れる

`appsettings.json` の `Hermes.ResponseTimeoutMs` を増やしてください (例: `3000`)。

### Claude Code インポートでセッションが表示されない

`~/.claude/projects/` ディレクトリが存在し、`.jsonl` ファイルが含まれているか確認してください。
