# codex_metrix_and_analyze
Codexの動作について欲しいメトリクスを取得し、解析する。最初はフックのローカルファイル出力から始め、OpenTelemetryなどの仕組みまで強化していく想定。

## Codex agent usage logger

`hooks/codex-agent-usage-logger.cs` は、Codex の hook payload を標準入力から受け取り、agent usage の観測用 JSONL ログへ追記する File-based apps 形式の C# フック。

主な機能は次のとおり。

- hook payload を JSON として読み取り、`hook_event_name`、`session_id`、`turn_id`、`agent_id`、`agent_type`、`model`、`cwd`、`permission_mode`、`transcript_path` などを記録する。
- `PostToolUse` などの tool event では、`tool_name`、`tool_input.command`、`tool_response` のサイズと先頭 500 文字の preview を記録する。
- `SubagentStart` で開始時刻を state file に保存し、`SubagentStop` で同じ `session_id` と `agent_id` の state を読み、`duration_ms` を算出する。
- 出力ログは日別ファイルへ分割する。既定では `agent-usage.jsonl` を基準に、実際の追記先は `agent-usage-yyyy-MM-dd.jsonl` になる。
- 複数プロセスから同時に追記されても壊れにくいよう、ログファイルごとの named mutex を使って追記する。
- JSON parse や処理中の例外は通常ログとは別の error log に JSONL 形式で記録し、hook 自体は終了コード `0` を返す。

既定の出力先は `CODEX_HOME` があればその配下、なければユーザープロファイル配下の `.codex` を使う。

| 環境変数 | 用途 | 既定値 |
|---|---|---|
| `CODEX_HOME` | Codex home の基準ディレクトリ | `%USERPROFILE%\.codex` |
| `CODEX_AGENT_USAGE_LOG` | usage log の基準パス | `%CODEX_HOME%\logs\agent-usage.jsonl` |
| `CODEX_AGENT_USAGE_STATE` | `SubagentStart` の開始時刻 state 保存先 | `%CODEX_HOME%\hook-state` |
| `CODEX_AGENT_USAGE_ERROR_LOG` | フック処理エラーの出力先 | `%CODEX_HOME%\logs\agent-usage-error.log` |

ログ 1 行の主なフィールドは次のとおり。

- `recorded_at`: フックが記録した UTC 時刻。
- `event`: `hook_event_name` の値。例: `SubagentStart`、`SubagentStop`、`PostToolUse`。
- `session_id` / `turn_id` / `agent_id` / `agent_type` / `model`: Codex 側から渡された実行コンテキスト。
- `permission_mode` / `cwd` / `transcript_path`: 実行時の許可モード、作業ディレクトリ、transcript path。
- `duration_ms`: `SubagentStop` で算出できた subagent 実行時間。開始 state がない場合は `null`。
- `tool_name` / `command`: tool 名と、`tool_input.command` がある場合のコマンド文字列。
- `tool_response_size` / `tool_response_preview`: tool response の JSON 文字列長と先頭 500 文字。
- `raw_payload_keys`: 受け取った payload の top-level key 一覧。

## フックのインストール

フック本体の導入・確認・削除を行うスクリプトは次の通り。

- `scripts/apply-hooks-config.cs`
  - `~/.codex/hooks.json` を生成
  - `~/.codex/hooks/codex-agent-usage-logger.exe` を NativeAOT publish して配置
  - 作業内容を `~/.codex/codex-metrix-and-analyze.manifest.json` に記録
- `scripts/remove-hooks-config.cs`
  - `~/.codex/codex-metrix-and-analyze.manifest.json`（優先）と `~/.codex/codex-local-config.manifest.json`（後方互換）を参照してアンインストール
  - バックアップがあれば復元、なければ導入時 hash が一致した場合のみ削除
- `scripts/test-hook.cs`
  - 一時 `CODEX_HOME` でインストール・実行・ログ確認までの smoke test を実施

```powershell
dotnet run --file scripts\apply-hooks-config.cs -- --dry-run
dotnet run --file scripts\apply-hooks-config.cs -- --force
dotnet run --file scripts\test-hook.cs
dotnet run --file scripts\remove-hooks-config.cs -- --dry-run
dotnet run --file scripts\remove-hooks-config.cs
```

インストール時は `--force` で既存ファイル上書き、`--backup-dir` でバックアップ先を指定できる。

## Codex agent usage report

`scripts/codex-agent-usage-report.cs` は、`hooks/codex-agent-usage-logger.cs` が出力した JSONL ログを読み取り、Markdown または JSON の利用状況レポートを生成する File-based apps 形式の C# スクリプト。

既定では `CODEX_AGENT_USAGE_LOG`、または `%CODEX_HOME%\logs\agent-usage.jsonl` を基準に入力ログを探す。基準ファイルがない場合は、当日の日別ログ `agent-usage-yyyy-MM-dd.jsonl`、それもなければ同じ命名規則の最新ログを対象にする。

```powershell
dotnet run --file scripts\codex-agent-usage-report.cs -- --help
```

基本例:

```powershell
dotnet run --file scripts\codex-agent-usage-report.cs -- --timezone Asia/Tokyo
```

期間と作業ディレクトリで絞り込み、Markdown をファイルへ出力する例:

```powershell
dotnet run --file scripts\codex-agent-usage-report.cs -- `
  --from 2026-06-08T00:00:00+09:00 `
  --to 2026-06-08T23:59:59+09:00 `
  --timezone Asia/Tokyo `
  --cwd-contains codex_metrix_and_analyze `
  --output reports\codex-agent-usage-2026-06-08.md
```

複数ログを明示し、JSON で出力する例:

```powershell
dotnet run --file scripts\codex-agent-usage-report.cs -- `
  --log "$env:CODEX_HOME\logs\agent-usage-2026-06-07.jsonl" `
  --log "$env:CODEX_HOME\logs\agent-usage-2026-06-08.jsonl" `
  --format json `
  --output reports\codex-agent-usage.json
```

### レポート内容

Markdown 出力には、次のセクションが含まれる。

- `Overview`: parse 済み record 数、parse error 数、session 数、turn 数、parent/unassigned の `apply_patch` 回数、未完了 subagent run 数、孤立した `SubagentStop` 数、subagent 総実行時間。
- event counts: event 名ごとの件数。
- `By model`: model ごとの record 数、tool use 数、subagent run 数、subagent 実行時間、session 数。
- `By agent type`: agent type ごとの subagent run 数、tool use 数、`Bash` 数、`apply_patch` 数、合計/平均 subagent 実行時間。
- `Subagent runs`: subagent run ごとの開始、終了、agent type、agent ID、model、duration、tool use 数、`apply_patch` 数。
- `Timeline`: bucket ごとの total events、tool uses、`Bash`、`apply_patch`、subagent start/stop 数。
- `Top commands`: `PostToolUse` の `command` を正規化した上位コマンド。
- `Workflow check`: `slice-prep`、`slice-impl`、`cross-slice-verification-kernel`、`residual-decision-gate`、parent の direct `apply_patch`、期待 model との一致を確認する簡易チェック。
- `Parse errors (sample)`: JSONL parse error がある場合の先頭 20 件。
- `Not available in current log schema`: 現在のログ schema では算出できない項目。

現在のログ schema では、token usage、command 単位の tool 実行時間、`Bash` exit code は取得できない。

### オプション

| オプション | 説明 | 既定値 |
|---|---|---|
| `--help`, `-h`, `/?` | usage を表示して終了する。 | - |
| `--log <path>` | 入力 JSONL ログファイル。複数回指定できる。 | 自動検出 |
| `--from <datetime>` | この日時以降を集計する。ISO-8601 形式を推奨。 | 開始制限なし |
| `--to <datetime>` | この日時以前を集計する。ISO-8601 形式を推奨。 | 終了制限なし |
| `--timezone <value>` | timeline bucket と表示用 timezone。`local`、`utc`、`Asia/Tokyo` などを指定する。 | `local` |
| `--bucket <value>` | timeline bucket 幅。`5m`、`15m`、`30m`、`1h` のいずれか。 | `15m` |
| `--session-id <id>` | `session_id` が一致する record だけを集計する。 | 指定なし |
| `--cwd-contains <text>` | `cwd` に指定文字列を含む record だけを集計する。大文字小文字は区別しない。 | 指定なし |
| `--expected-parent-model <value>` | parent/unassigned の最頻 model が期待値と一致するか `Workflow check` で確認する。 | 指定なし |
| `--expected-subagent-model <value>` | subagent の最頻 model が期待値と一致するか `Workflow check` で確認する。 | 指定なし |
| `--top <n>` | `Top commands` に出す件数。1 以上。 | `20` |
| `--format <value>` | 出力形式。`markdown` または `json`。 | `markdown` |
| `--output <path>` | 出力先ファイル。未指定なら標準出力へ書く。 | 標準出力 |

日時オプションは `DateTimeOffset` として解釈される。`--from` と `--to` の両方を指定する場合、`--from` が `--to` より後だとエラーになる。
