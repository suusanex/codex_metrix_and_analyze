# トラブルシューティング

## インストールで上書きに失敗する

`scripts/apply-hooks-config.cs` は既存の対象ファイルが差分ありかつ `--force` 未指定の場合、実行を中断する。

以下を順番に実行して確認する。

```powershell
dotnet run --file scripts/apply-hooks-config.cs -- --dry-run
dotnet run --file scripts/apply-hooks-config.cs -- --force
dotnet run --file scripts/remove-hooks-config.cs -- --dry-run
```

`--force` を使う前に、インストーラ表示のバックアップ先を確認しておくと安全。

## Codex でフックが動かない

次を確認する。

- `~/.codex/hooks.json` のコマンドが `codex-agent-usage-logger.exe` を指しているか
- `~/.codex/hooks.json` に `C:\\Users\\suusa\\.codex\\hooks\\codex-agent-usage-logger.exe` のような絶対パスが含まれているか
- `~/.codex/hooks/codex-agent-usage-logger.exe` が存在するか
- Codex の `/hooks` で該当コマンドフックが表示され、Trust 済みか

フック内部でのエラーは以下を確認する。

```text
~/.codex/logs/agent-usage-error.log
```

このファイルはエラー時のみ更新される。

## スモークテストで失敗する

以下を実行する。

```powershell
dotnet run --file scripts/test-hook.cs
```

このテストは一時 `CODEX_HOME` を使うため、実運用ログ領域には影響しない。

## インストール時に NativeAOT publish が失敗する

この実装では fallback で `dotnet run` に戻らず、失敗時は即終了する。

- リポジトリのルートで `dotnet publish hooks\\codex-agent-usage-logger.cs -c Release --use-current-runtime` が通るか
- 実機で NativeAOT のビルドが可能か
- セキュリティソフトなどが一時 publish 出力をブロックしていないか

## SubagentStop に duration が入らない

`session_id` と `agent_id` が一致した `SubagentStart` が検知されていない場合、`duration_ms` は `null` になる。`hook-state` が中間で削除されると発生しやすい。

## 現状のユーザー設定マージについて

`codex/config.extra.toml` は現行フェーズでは `~/.codex/config.toml` へ自動反映しない。必要なセクションだけ手動でコピーし、`projects.*` や `hooks.state.*`、Marketplace / plugin state などの動的情報は実設定側で管理する。
