# codex_metrix_and_analyze

Codex hook payload をローカル観測イベントとして保存し、model / agent / tool / repository / timeline 単位で work share を分析するための File-based apps 集。

## 観測 logger v2

`hooks/codex-agent-usage-logger.cs` は hook payload を受け取り、schema version `2.0` の観測イベントを日別 JSONL に追記する。

主な挙動は次の通り。

- 既定の出力先は `%CODEX_HOME%\logs\agent-observations-YYYY-MM-DD.jsonl`
- `CODEX_AGENT_OBSERVATION_LOG` / `STATE` / `ERROR_LOG` を優先し、未指定時は legacy `CODEX_AGENT_USAGE_*` も読む
- `PreToolUse` / `PostToolUse` を `tool_use_id` で相関し、`tool_elapsed_ms` と `tool_correlation_status` を記録する
- `SubagentStart` / `SubagentStop` を `session_id + agent_id` で相関し、`subagent_duration_ms` と `subagent_correlation_status` を記録する
- tool input / response / raw payload は既定では全文保存せず、redacted preview / size / hash を保存する
- repo / git / transcript metadata、payload key 一覧、unknown key 一覧、capture policy、trace/span IDs を保存する
- hook 自身の例外は error log に `Exception.ToString()` を残し、終了コード `0` を返す

### 環境変数

| 環境変数 | 用途 | 既定値 |
|---|---|---|
| `CODEX_HOME` | Codex home の基準ディレクトリ | `%USERPROFILE%\.codex` |
| `CODEX_AGENT_OBSERVATION_LOG` | v2 観測ログの基準パス | `%CODEX_HOME%\logs\agent-observations.jsonl` |
| `CODEX_AGENT_OBSERVATION_STATE` | tool / subagent 相関 state 保存先 | `%CODEX_HOME%\hook-state\agent-observations` |
| `CODEX_AGENT_OBSERVATION_ERROR_LOG` | logger error log の基準パス | `%CODEX_HOME%\logs\agent-observations-error.log` |
| `CODEX_AGENT_OBSERVATION_CAPTURE_RAW_PAYLOAD` | redacted raw payload 全文を保存する opt-in | `false` |
| `CODEX_AGENT_OBSERVATION_CAPTURE_FULL_TOOL_INPUT` | redacted tool input 全文を保存する opt-in | `false` |
| `CODEX_AGENT_OBSERVATION_CAPTURE_FULL_TOOL_RESPONSE` | redacted tool response 全文を保存する opt-in | `false` |
| `CODEX_AGENT_OBSERVATION_CAPTURE_PROMPT_PREVIEW` | prompt preview を保存する opt-in | `false` |
| `CODEX_AGENT_OBSERVATION_TRANSCRIPT_SNAPSHOT` | transcript hash を取る実験 opt-in | `false` |

legacy 互換:

- `CODEX_AGENT_USAGE_LOG`
- `CODEX_AGENT_USAGE_STATE`
- `CODEX_AGENT_USAGE_ERROR_LOG`

### 記録する代表フィールド

- 共通: `schema_version`, `record_id`, `recorded_at`, `event`, `event_category`, `session_id`, `turn_id`, `agent_id`, `agent_type`, `model`, `permission_mode`, `cwd`
- repo / transcript: `repo_root`, `repo_name`, `git_branch`, `git_commit`, `transcript_path`, `transcript_file_size`, `transcript_file_mtime`, `transcript_file_hash`
- payload 由来: `raw_payload_keys`, `unknown_payload_keys`, `raw_payload_size`, `raw_payload_hash`
- tool 相関: `tool_use_id`, `tool_name`, `tool_category`, `tool_input_size`, `tool_input_hash`, `tool_input_preview`, `command_redacted`, `command_kind`, `command_risk`, `tool_response_size`, `tool_response_hash`, `tool_response_preview`, `tool_result_class`, `tool_elapsed_ms`, `tool_correlation_status`
- subagent 相関: `subagent_run_id`, `subagent_started_at`, `subagent_stopped_at`, `subagent_duration_ms`, `subagent_correlation_status`
- 観測ポリシー: `capture_policy`
- 将来の OTLP 変換向け: `trace_id`, `turn_span_id`, `subagent_span_id`, `tool_span_id`, `parent_span_id`, `span_id`

詳細は [docs/event-schema.md](/C:/Users/suusa/.codex/worktrees/e1ef/codex_metrix_and_analyze/docs/event-schema.md) を参照。

## フックの導入

`scripts/apply-hooks-config.cs` は logger の publish と `hooks.json` 更新を行う。`scripts/remove-hooks-config.cs` は manifest を使ってアンインストールする。`scripts/test-hook.cs` は一時 `CODEX_HOME` 上で smoke test を行う。

```powershell
dotnet run --file scripts\apply-hooks-config.cs -- --dry-run
dotnet run --file scripts\apply-hooks-config.cs -- --force
dotnet run --file scripts\test-hook.cs
dotnet run --file scripts\remove-hooks-config.cs -- --dry-run
dotnet run --file scripts\remove-hooks-config.cs
```

## レポート

`scripts/codex-agent-usage-report.cs` は v2 `agent-observations-*.jsonl` と旧 `agent-usage-*.jsonl` の両方を読み、Markdown または JSON の派生レポートを生成する。

既定では次をこの順で探す。

1. `CODEX_AGENT_OBSERVATION_LOG`
2. `CODEX_AGENT_USAGE_LOG`
3. `%CODEX_HOME%\logs\agent-observations*.jsonl`
4. `%CODEX_HOME%\logs\agent-usage*.jsonl`

```powershell
dotnet run --file scripts\codex-agent-usage-report.cs -- --help
```

基本例:

```powershell
dotnet run --file scripts\codex-agent-usage-report.cs -- --timezone Asia/Tokyo
```

複数ログを指定し、JSON を出す例:

```powershell
dotnet run --file scripts\codex-agent-usage-report.cs -- `
  --log "$env:CODEX_HOME\logs\agent-observations-2026-06-09.jsonl" `
  --log "$env:CODEX_HOME\logs\agent-usage-2026-06-08.jsonl" `
  --format json `
  --output reports\codex-observation-report.json
```

手入力した limit delta を model share へ按分する例:

```powershell
dotnet run --file scripts\codex-agent-usage-report.cs -- `
  --from 2026-06-09T00:00:00+09:00 `
  --to 2026-06-09T23:59:59+09:00 `
  --limit-delta 12.5 `
  --limit-unit percent `
  --allocation-basis weighted
```

### 出力セクション

JSON 出力は次の top-level section を持つ。

- `summary`
- `model_rows`
- `agent_type_rows`
- `tool_rows`
- `repository_rows`
- `timeline_rows`
- `correlation_issues`
- `allocation`
- `warnings`
- `not_available`

主な集計値:

- events
- tool invocations
- matched tool invocations
- missing correlation counts
- total tool elapsed
- total subagent duration
- Bash / apply_patch counts
- tool input / response bytes
- command kind counts
- weighted work score / share

`allocation` は `--limit-delta` または `--limit-before` / `--limit-after` 指定時のみ入る。これは観測ログに基づく推定按分であり、課金や実リミットの確定値ではない。

### オプション

| オプション | 説明 | 既定値 |
|---|---|---|
| `--log <path>` | 入力 JSONL ログ。複数回指定可 | 自動検出 |
| `--from <datetime>` | 集計開始時刻 | 制限なし |
| `--to <datetime>` | 集計終了時刻 | 制限なし |
| `--timezone <value>` | 表示 / bucket 用 timezone | `local` |
| `--bucket 5m\|15m\|30m\|1h` | timeline bucket 幅 | `15m` |
| `--session-id <id>` | session filter | 指定なし |
| `--cwd-contains <text>` | cwd substring filter | 指定なし |
| `--limit-before <number>` | 手入力の観測前値 | 指定なし |
| `--limit-after <number>` | 手入力の観測後値 | 指定なし |
| `--limit-delta <number>` | 観測差分 | 指定なし |
| `--limit-unit <value>` | `percent` / `credits` / `points` / `unknown` | `unknown` |
| `--allocation-basis <value>` | `weighted` / `duration` / `tool-elapsed` / `tool-count` / `response-size` | `weighted` |
| `--format markdown\|json` | 出力形式 | `markdown` |
| `--output <path>` | 出力先ファイル | 標準出力 |

## サンプル

- v2 sample: [examples/agent-observations.sample.jsonl](/C:/Users/suusa/.codex/worktrees/e1ef/codex_metrix_and_analyze/examples/agent-observations.sample.jsonl)
- legacy sample: [examples/agent-usage.sample.jsonl](/C:/Users/suusa/.codex/worktrees/e1ef/codex_metrix_and_analyze/examples/agent-usage.sample.jsonl)
