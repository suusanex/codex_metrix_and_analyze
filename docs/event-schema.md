# Observation Event Schema v2

`agent-observations-YYYY-MM-DD.jsonl` は append-only の観測イベントログで、1 行につき 1 JSON object を保存する。

既定の基準パスは `%CODEX_HOME%\logs\agent-observations.jsonl` で、実際の出力先は日付付き `agent-observations-YYYY-MM-DD.jsonl` になる。legacy `agent-usage-YYYY-MM-DD.jsonl` は report 側で読み取り互換を持つ。

## 共通フィールド

代表的な共通フィールドは次の通り。

- `schema_version`: 現在は `2.0`
- `logger_name`: `codex-agent-observation-logger`
- `logger_version`: logger 実装 version
- `record_id`: 1 event 1 ID
- `recorded_at`: logger が観測した UTC ISO-8601 時刻
- `event`: hook event 名
- `event_category`: `session` / `turn` / `subagent` / `tool` / `permission` / `compact` / `unknown`
- `session_id`, `turn_id`, `agent_id`, `agent_type`, `model`, `permission_mode`, `cwd`
- `repo_root`, `repo_name`, `git_branch`, `git_commit`
- `transcript_path`, `transcript_file_size`, `transcript_file_mtime`, `transcript_file_hash`, `transcript_status`
- `raw_payload_keys`, `unknown_payload_keys`, `raw_payload_size`, `raw_payload_hash`
- `capture_policy`
- `host`
- `trace_id`, `turn_span_id`, `subagent_span_id`, `tool_span_id`, `parent_span_id`, `span_id`

### `capture_policy`

logger は保存方針を event ごとに埋め込む。

- `redaction_enabled`
- `hash_basis`
- `capture_raw_payload`
- `capture_full_tool_input`
- `capture_full_tool_response`
- `capture_prompt_preview`
- `capture_transcript_snapshot`
- `command_storage`
- `preview_max_chars`
- `classification_version`

### `host`

個人情報を最小化するため、`host` には匿名化寄りの metadata を入れる。

- `host_id`
- `os_description`
- `os_architecture`
- `process_architecture`
- `framework_description`
- `process_id`
- `process_name`

## Tool 関連フィールド

`PreToolUse` / `PostToolUse` では次を扱う。

- `tool_use_id`
- `tool_name`
- `tool_category`
- `tool_input_size`, `tool_input_hash`, `tool_input_preview`
- `command`, `command_redacted`, `command_hash`, `command_normalized`
- `command_kind`
- `command_risk`
- `tool_response_size`, `tool_response_hash`, `tool_response_preview`
- `tool_result_class`
- `tool_elapsed_ms`
- `tool_correlation_status`

`tool_correlation_status` は主に次を取る。

- `matched`
- `missing_pre`
- `missing_post`（report 側で検出）
- `duplicate`
- `unknown`

## Subagent 関連フィールド

`SubagentStart` / `SubagentStop` では次を扱う。

- `subagent_run_id`
- `subagent_started_at`
- `subagent_stopped_at`
- `subagent_duration_ms`
- `subagent_correlation_status`
- `duration_ms`

`duration_ms` は legacy report 互換のため残している。値は `subagent_duration_ms` と同じ。

`subagent_correlation_status` は主に次を取る。

- `matched`
- `missing_start`
- `missing_stop`（report 側で検出）
- `duplicate`
- `unknown`

## Event ごとの補助フィールド

- `SessionStart`: `source`
- `Stop`: `stop_reason`
- `PermissionRequest`: `requested_tool`, `permission_decision`, `permission_reason`, `permission_escalation_kind`
- `PreCompact` / `PostCompact`: `compact_trigger`
- `UserPromptSubmit`: `prompt_size`, `prompt_hash`, `prompt_preview`（opt-in）

## Redaction / privacy

既定動作:

- raw payload 全文は保存しない
- tool input / response 全文は保存しない
- command / preview / hash は redaction 済みの値だけを保存する
- transcript 本文は保存しない

opt-in:

- `CODEX_AGENT_OBSERVATION_CAPTURE_RAW_PAYLOAD`
- `CODEX_AGENT_OBSERVATION_CAPTURE_FULL_TOOL_INPUT`
- `CODEX_AGENT_OBSERVATION_CAPTURE_FULL_TOOL_RESPONSE`
- `CODEX_AGENT_OBSERVATION_CAPTURE_PROMPT_PREVIEW`
- `CODEX_AGENT_OBSERVATION_TRANSCRIPT_SNAPSHOT`

opt-in を有効にしても redaction は常に通る。

## State

logger は `%CODEX_HOME%\hook-state\agent-observations` 配下に相関 state を持つ。

- `tool\*.json`: `PreToolUse` と `PostToolUse` の相関用
- `subagent\*.json`: `SubagentStart` と `SubagentStop` の相関用

state file 名は key の hash から生成する。7 日より古い state は logger 実行時に掃除する。

## Error log

logger 自身の例外は `%CODEX_HOME%\logs\agent-observations-error-YYYY-MM-DD.log` へ JSONL 追記する。hook の exit code は原則 `0` のまま維持する。
