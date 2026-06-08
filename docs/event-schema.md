# Agent Usage Event Schema

`agent-usage.jsonl` は 1 行あたり 1 件の JSON オブジェクトとして保存される。

## 記録フィールド

- `recorded_at`: ISO 8601 形式の UTC 時刻
- `event`: `SessionStart`、`PostToolUse`、`SubagentStart`、`SubagentStop` などの hook イベント名
- `session_id`: `session_id` があれば保存
- `turn_id`: `turn_id` があれば保存
- `agent_id`: `agent_id` があれば保存
- `agent_type`: `agent_type` があれば保存
- `model`: `model` があれば保存
- `permission_mode`: `permission_mode` があれば保存
- `cwd`: `cwd` があれば保存
- `transcript_path`: `transcript_path` があれば保存
- `duration_ms`: `SubagentStart` と対応が取れる `SubagentStop` のみ記録
- `tool_name`: `PostToolUse` の `tool_name`
- `command`: `tool_input.command` があれば抽出
- `tool_response_size`: `tool_response` のシリアライズ文字列長
- `tool_response_preview`: `tool_response` の先頭 500 文字
- `raw_payload_keys`: 後続デバッグ用にトップレベルキー名をソートして保存

## State ファイル

`SubagentStart` と `SubagentStop` は `~/.codex/hook-state/*.json` を使って実行時間を算出する。State ファイルはローカル実行時データで、git 管理対象外として扱う。

## エラーログ

フックでエラーが発生すると `~/.codex/logs/agent-usage-error.log` に 1 行 1 件の JSON オブジェクトとして追記される。

エラーログは通常の JSONL ログとは別ファイルで、エラー時のみ更新される。

## プライバシー

- フックは `tool_response` の全文を保存せず、`tool_response_preview` のみ保存する
- `raw_payload_keys` はトップレベルキー名のみ保存する
- 生ログとトランスクリプトはローカルにのみ残し、git 管理しない
