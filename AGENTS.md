# 共通

1. 原則として選択する実装方式は.NET＆C#とし、スクリプトを作る場合はFile-based Appsとする
2. ドキュメント（コミットログやissueを含む）は日本語で記載する
3. ソースコード上のコメント・XMLコメントは開発者向けのため日本語で記載し、ソースコードやログ出力は英語で記載する
4. 原則として処理失敗時のフォールバックは行わず、処理が失敗したことを示すエラー・例外を返す実装とすること。
5. 全ての例外は、トレースログへException.ToString()の内容を出力すること。そのため、例外を再throwせずに捨てる場合は、その場でトレースログを出力すること。
6. UnitTest と（CIで走る）IntegrationTest は、実OS環境（レジストリ、SetupAPI、サービス、ドライバ、デバイス等）を変更しない。
    1. OS依存処理は必ずインターフェースで抽象化し、テスト時はスタブ／モックを注入する（例: ISetupApiWrapper）。
    2. CIで実行される統合テストもスタブを使用し、管理者権限や実OS変更を要求しない。
7. 原則としてリフレクションを使用しない。もしも実装上リフレクションを使用するべきだと判断する場合は、コードコメントで必要な理由を説明した上で、チャットでもリフレクションを使用した事実と理由を説明すること。
8. 独自実装に入る前に、既存コード、BCL、フレームワーク専用 API、NuGet で取得可能な OSS を調査し、再利用または採用を優先して検討すること。
9. 独自実装を採用する場合は、既存コード・BCL・フレームワーク専用 API・OSS を採用しなかった理由を明記すること。


# Plan網羅チェック・残件判定フロー

次のようにカスタムエージェントを使用した開発フローを使用します。

1. `plan-kernel.agent.md`
2. `change-risk-triage.agent.md`
3. `implementation-contract-kernel.agent.md`（implementation-realization risk がある場合）
4. `implementation-contract-review-kernel.agent.md`（contract が non-trivial の場合）
5. `runtime-contract-kernel.agent.md`
6. `test-design-kernel.agent.md`
7. `implementation-handoff-review.agent.md`
8. `implementation-execution.agent.md` または人間主導で bounded parent Plan pass を実行
9. 必要に応じて `code-review-focus-kernel.agent.md`
10. human code review
11. `verification-kernel.agent.md`
12. 未解決がある場合は `coverage-gap-triage.agent.md`
13. `residual-decision-gate.agent.md`
14. FixNow items がある場合だけ `coverage-gap-resolution-slice.agent.md`
15. 必要に応じて `verification-kernel.agent.md` と `residual-decision-gate.agent.md` を再実行

各 agent は 1 回の bounded な実行を行い、未解決項目は成果物に残して停止します。「直るまで修正し続ける」ことは目的ではありません。

## Agent artifacts language policy

- `plans/*.md`、kernel/review/triage artifact、handoff artifact は日本語で記載する。
- `.github/agents/*.agent.md` のテンプレート見出しが英語でも、成果物では可能な限り日本語見出しへ翻訳する。
- Contract ID、Test Point ID、status vocabulary、CLI option、型名、ファイルパスなどの識別子は英語のままでよい。
- Required output structure の項目名を維持する必要がある場合でも、本文・説明・表の Notes は日本語で書く。


<!-- codex-first:start -->
## Codex-first

このリポジトリには Codex-first コスト意識ルーティングの導入手順を追加した。
- 利用者は process 名・agent 名・model 名・full-coverage 分岐を選ぶ必要がない。
- repo-local の AGENTS.md / 制約は引き続き最優先で読む。
- `.agents/skills/codex-first-cost-router/SKILL.md` の振る舞いで source of truth、repo rules、既存 artifact、state artifact を確認する。
- README の指示と `.codex/config.toml` / `.codex/agents/*.toml` / `.agents/skills/codex-first-cost-router/SKILL.md` / `templates/*.md` を使って `codex-first` 標準ルートを使う。
- state artifact には Routing Plan、Edit Permission、Agent Usage Ledger、DelegationCompliance を記録する。
- state artifact では execution_mode と、model tier / configured model / hook model / reported model / effective model を分けて記録する。
- READY 後の通常実装は `standard-implementer`、通常 verification は `standard-verifier` へ serial delegation する。
- `DelegationRequired = Yes` の gate は observed run または explicit human approval 付き `ParentDirectExecutionException` がない限り成功扱いしない。
- 親が委譲予定の作業を直接実行した場合、cost-saving delegation 成功として扱わない。
- write-heavy parallel editing を標準化しないことは、親が直接実装してよいことを意味しない。

`codex-first-start.ps1` は起動時のみ CODEX_HOME を切り替える一時 launcher なので、
リポジトリごとの標準利用では、本インストーラで `.codex` と `AGENTS.md` を揃える。

参考: D:\Data\git\coding_agent_plan_and_verify_process\apm-packages\codex-first-ai-development-process\scripts\codex-first-start.ps1

<!-- codex-first:end -->
