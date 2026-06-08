# Codex metrics observability v2 実装 Plan

## Source of Truth

- `D:\Temp\codex_metrics_observability_v2_requirements.md`
- Repo rules: `AGENTS.md`
- Process: `.agents/skills/codex-first-cost-router/SKILL.md`

## 目的

Codex hook payload を、後続分析・usage/cost 突き合わせ・OpenTelemetry 変換に耐える v2 観測イベント JSONL として保存し、v2/旧形式のログから work share と limit delta allocation を出力できるようにする。

## 受け入れ条件

- `PreToolUse` / `PostToolUse` / `SubagentStart` / `SubagentStop` を schema version `2.0` の JSONL として記録できる。
- `tool_use_id` による tool invocation 相関で、正常時は `tool_elapsed_ms`、orphan `PostToolUse` は `missing_pre`、未完了 `PreToolUse` はレポート側で `missing_post` として扱える。
- `agent_id` による subagent 相関で、正常時は `subagent_duration_ms`、orphan `SubagentStop` は `missing_start` として扱える。
- 全イベントに session / turn / agent / model / cwd / permission / transcript / repo / git / payload key / size / hash / capture policy / host / trace/span 系フィールドを可能な範囲で保存する。
- 既定で full tool input/response/raw payload は保存せず、preview/hash/size と主要抽出フィールドを保存する。
- preview と command は secret らしい値を redaction する。
- v2 JSONL は日別 `agent-observations-YYYY-MM-DD.jsonl` に追記され、旧 `CODEX_AGENT_USAGE_*` 環境変数も互換入力として扱う。
- レポートは v2 JSONL と旧 `agent-usage-*.jsonl` を読み取り、Markdown と JSON の両方を出力できる。
- レポートは model / agent_type / tool_name / repository / timeline の work share、correlation issues、limit delta allocation を出力できる。
- smoke/unit 相当のテストで正常 tool 相関、missing_pre、missing_post、subagent 正常相関、orphan stop、並列追記、redaction、旧ログ読み取り互換を確認する。

## 非ゴール

- ChatGPT/Codex アプリ上のリミット消費や AI credit を正確に再現しない。
- transcript の不安定形式に依存した token usage 算出は行わない。
- OpenAI usage/cost API や OpenTelemetry Collector への自動送信は実装しない。

## 実装スコープ

- `hooks/codex-agent-usage-logger.cs`
- `scripts/codex-agent-usage-report.cs`
- `scripts/test-hook.cs`
- `README.md`
- `scripts/apply-hooks-config.cs`
- `codex/hooks.json`
- 必要に応じて `docs/event-schema.md` と `examples/*.jsonl`

## 実装方針

- File-based Apps 形式を維持し、`.csproj` は作成しない。
- 既存の単一ファイル構成を維持し、BCL の `System.Text.Json`、`System.Security.Cryptography`、`Mutex`、`Process` を再利用する。
- NuGet 追加は行わない。JSONL writer、hash、mutex、git metadata 取得は BCL と既存パターンで十分に実装できるため。
- 独自実装が必要な redaction / command classification / correlation state は、外部依存を増やさず再計算可能な derived fields と `classification_version` で扱う。
- hook 処理例外は Codex 側の作業を止めないため終了コード `0` を維持し、例外詳細は `Exception.ToString()` を error log と trace に残す。

## 完了条件

- 実装委譲の成果が取り込まれている。
- `dotnet run --file scripts\test-hook.cs` が成功する。
- v2 sample log に対して `scripts\codex-agent-usage-report.cs` の Markdown/JSON 出力が成功する。
- codex-first state の DelegationCompliance が PASS または明示的な accepted exception になる。
