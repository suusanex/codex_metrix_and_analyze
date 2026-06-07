---
name: token-aware-full-coverage-3layer
description: Plan網羅チェック・残件判定フローで full-coverage 診断後の plan-slice-decomposition を、Codex の親エージェント・slice-prep・slice-impl の3層運用で安全に進めるための skill。full-coverage、plan-slice-decomposition、cross-slice-verification-kernel、residual-decision-gate が関係するときだけ使う。skill 名は互換用の legacy invocation として残す。
---

# Plan網羅チェック full-coverage 3層運用 Skill

<!--
Copyright (c) 2026 suusanex (GitHub UserName)
SPDX-License-Identifier: CC-BY-4.0
License: https://creativecommons.org/licenses/by/4.0/
Source: https://github.com/suusanex/coding_agent_plan_and_verify_process
-->

## 目的

この skill は、Plan網羅チェック・残件判定フローで `change-risk-triage.agent.md` が `full-coverage` を診断し、`plan-slice-decomposition.agent.md` によって複数の slice が作成された後に使います。

`token-aware-full-coverage-3layer` という skill 名と `$token-aware-full-coverage-3layer` の起動例は互換用の legacy invocation です。新しい作業では本文の概念を Plan網羅チェック・残件判定フロー、Guardrail Focus、Residual Decision Gate として扱ってください。

目的は、広い parent Plan を bounded な slice 実行に戻しつつ、slice 間の runtime contract、field continuity、production wiring、parent acceptance condition を失わないことです。

この skill は「全部をサブエージェントに丸投げする」ためのものではありません。親エージェントが設計整合を握り、サブエージェントには bounded な準備・実装・検証だけを任せます。

## 発動条件

次のいずれかに当てはまる場合に使ってください。

- `change-risk-triage.agent.md` が `full-coverage` を推奨した
- `plan-slice-decomposition.agent.md` の出力を Codex で実行したい
- full-coverage decomposition 由来の slice を、並列化しつつ安全に進めたい
- cross-slice contract (`XC-xxx`) を含む Plan網羅チェック・残件判定フローを進めたい

次の場合は使いません。

- 1つの bounded Plan を通常の Plan網羅チェック・残件判定フローで進めれば足りる
- `fix-slice` だけの小さな既知 gap 修正である
- Full autonomous Plan-first flow を明示的に選んでいる
- 人間が各 artifact を手作業で作成し、Codex には単発実装だけを依頼する

## 必須入力

親エージェントは、少なくとも次を source artifact として読む必要があります。

- parent bounded Plan
- parent `change-risk-triage.agent.md` の出力
- `plans/<ticket-or-slug>-slice-decomposition.md`
- 各 executable slice artifact: `plans/<ticket-or-slug>-slice-SL-xxx.md`
- 既存の関連 docs / architecture docs / domain docs（必要な範囲のみ）

repository 全体を無差別に読んではいけません。必要な artifact と関係ファイルに限定してください。

## 3層運用の全体像

```text
Layer 1: 親エージェント
  parent Plan / triage / slice decomposition を読み、
  slice 実行表・依存関係・parallel 可否・parent review gate を管理する。

Layer 2: slice-prep subagent
  slice artifact を bounded Plan として扱い、
  per-slice risk / contract / test design artifact を下書きする。
  ここでは実装しない。

Layer 3: slice-impl subagent
  親が READY と判定した slice だけを実装し、
  slice-local verification-kernel まで進めて停止する。

Final gate: 親エージェント
  全 slice の verification 結果を集約し、
  cross-slice-verification-kernel と residual-decision-gate を実行する。
```

## ExecutionMode と delegation invariant

親エージェントは開始時に `plans/<ticket-or-slug>-agent-usage-ledger.md` を作成または更新し、次のいずれかの `ExecutionMode` を必ず記録してください。

| ExecutionMode | 意味 | production code / tests 編集 |
| --- | --- | --- |
| `PREP_ONLY` | slice-prep と parent review gate までで停止する | 禁止 |
| `DELEGATED_IMPLEMENTATION` | READY slice を `slice-impl` に委譲して実装する | 親は禁止、`slice-impl` のみ可 |
| `PARENT_DIRECT_IMPLEMENTATION_EXCEPTION` | 例外的に親が直接実装する | 明示理由とユーザー承認が必要。3層委譲成功とは扱わない |

`DELEGATED_IMPLEMENTATION` では、親エージェントは production code / tests を直接編集してはいけません。親が直接編集してよいのは、原則として orchestration / review / usage ledger / final summary / handoff artifact だけです。

親が直接編集できる artifact の例:

- `plans/*-slice-execution-table.md`
- `plans/*-parent-review-gate.md`
- `plans/*-cross-slice-verification-kernel.md`
- `plans/*-residual-decision-gate.md`
- `plans/*-agent-usage-ledger.md`
- final summary / handoff artifact

委譲が必要な工程で custom agent / subagent を起動できない場合、親はその工程を自分で続行せず、`DelegationUnavailable` または `BlockedByMissingSliceImplDelegation` として停止してください。親直接実装は `PARENT_DIRECT_IMPLEMENTATION_EXCEPTION` と explicit human approval がある場合だけ許可されます。

## Layer 1: 親エージェント orchestration

親エージェントは最初に、`plan-slice-decomposition` の出力を実装指示ではなく「slice 実行候補」として扱ってください。

親エージェントは次を行います。

1. parent Plan の goal / non-goals / functional requirements / acceptance conditions を確認する。
2. parent triage の high-risk boundaries / parent-level runtime contract candidates / implementation-realization risk summary を確認する。
3. slice decomposition artifact から、各 slice の scope / non-goals / dependencies / related XC IDs / recommended profile / immediate next agent を抽出する。
4. `Cross-slice Contracts` と `Cross-slice field continuity` を抽出する。
5. parent-level contract mapping が消えていないか確認する。
6. slice 実行表を作る。
7. どの slice を並列で slice preparation に出せるかを仮決定する。
8. 同じ production wiring、shared DTO、DB schema、DI registration、config、public API、migration、durable state owner を触る可能性がある slice は並列実装させない。

### Slice 実行表の形式

親エージェントは、最初に次の表を作成してください。

```md
| Slice ID | Goal | Recommended profile | Blocking dependency | Shared ownership risk | Related XC IDs | Delegation required | Prep agent | Implementation allowed now? | Edit owner | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
```

`Implementation allowed now?` は初期状態では原則 `No` です。`plan-slice-decomposition` の出力だけで `Yes` にしてはいけません。

## Layer 2: slice preparation

親エージェントは、executable な slice ごとに `slice-prep` custom agent へ MUST delegate してください。

executable slice は、次のいずれかを満たす必要があります。

- `slice-prep` run が存在する。
- parent review gate が `BLOCKED` / `NEEDS_HUMAN_DECISION` / `TRIAGE_ONLY` として実装対象外にした。
- `PARENT_PREP_EXCEPTION` が agent usage ledger に明示されている。

`slice-prep` に渡す入力は、少なくとも次です。

- parent Plan
- parent triage output
- parent slice decomposition artifact
- assigned slice artifact
- assigned slice に関係する cross-slice contract excerpt
- assigned slice に関係する field continuity items
- この pass での bounded parent Plan pass / Guardrail Focus coverage / non-goals / stop condition

`slice-prep` は次を行います。

1. assigned slice artifact を bounded Plan として扱う。
2. per-slice `change-risk-triage` を実行する。
3. implementation-realization risk が `Present` または `Unclear` の場合、per-slice `implementation-contract-kernel` を下書きする。
4. implementation contract に non-trivial な判断がある場合、per-slice `implementation-contract-review-kernel` の下書きまたは review requirement を作る。
5. selected slice-local RC IDs について `runtime-contract-kernel` を下書きする。
6. `test-design-kernel` を下書きする。
7. 実装は行わない。
8. cross-slice contract を slice 内で完了扱いにしない。
9. 最後に `READY_FOR_PARENT_REVIEW`、`BLOCKED`、`NEEDS_HUMAN_DECISION` のいずれかを返す。

### slice-prep の出力形式

`slice-prep` は、親エージェントに次の形式で返してください。

```md
# Slice Preparation Result: SL-xxx

## Verdict

- Status: READY_FOR_PARENT_REVIEW / BLOCKED / NEEDS_HUMAN_DECISION
- Reason:

## Agent metadata

- Agent type: slice-prep
- Model:
- Reasoning effort:
- Parent authorization artifact:
- Delegation evidence:

## Generated / drafted artifacts

- Per-slice change-risk-triage:
- Implementation-contract-kernel:
- Implementation-contract-review-kernel:
- Runtime-contract-kernel:
- Test-design-kernel:

## Bounded parent Plan pass / Guardrail Focus

## Non-goals

## RC / TP / XC ledger

| ID | Kind | Owned / Consumed / Deferred | Notes |
| --- | --- | --- | --- |

## Production binding requirements

## Cross-slice risks to parent-review

## Unresolved items

## Stop condition
```

## Parent review gate

親エージェントは、すべての slice-prep 出力を実装前にレビューします。

親レビューでは次を確認してください。

- parent Plan の FR / AC が slice 群で保持されているか
- slice ごとの per-slice triage が parent triage と矛盾していないか
- implementation-realization risk が `Present` / `Unclear` なのに implementation-contract branch が省略されていないか
- runtime-contract-kernel と test-design-kernel が Plan の代替として扱われていないか
- `XC-xxx` の producer / consumer / required fields / mechanism が一致しているか
- field continuity の source artifact / producer output / consumer requirement が traceable か
- shared DTO / DB schema / DI / config / public API / migration / durable state の ownership が重複していないか
- parallel implementation してよい slice と、直列化すべき slice が分かれているか
- source evidence のない fabricated value が `Done` 扱いされていないか
- production binding requirement が test-only stub / fake で代替されていないか

親レビューの出力は次の形式にしてください。

```md
# Parent Review Gate

## Verdict per slice

| Slice ID | Verdict | Can implement now? | Parallel group | Blocking reason |
| --- | --- | --- | --- | --- |

## Cross-slice contract review

| XC ID | Producer | Consumer | Status | Notes |
| --- | --- | --- | --- | --- |

## Field continuity review

| Field / state / identifier | Required by | Source / producer | Consumer | Status | Notes |
| --- | --- | --- | --- | --- | --- |

## Implementation authorization

- Authorized slices:
- Serialized slices:
- Blocked slices:
- Human decision required:

## Parent instructions for slice-impl
```

`Can implement now?` が `No` の slice を `slice-impl` に渡してはいけません。

## Layer 3: implementation and verification

`DELEGATED_IMPLEMENTATION` mode では、親レビューで `Can implement now? = Yes` になった slice は必ず `slice-impl` custom agent に渡してください。親は READY slice を自分で実装してはいけません。

READY slice は、次の証跡を満たす必要があります。

- `slice-impl` run が存在する。
- `slice-impl` output が `Slice Implementation Result: SL-xxx` を持つ。
- `Agent type: slice-impl` / `Model` / `Reasoning effort` / `Parent authorization artifact` が記録されている。
- `Changed files` / `Checks run` / `Verification verdict` が記録されている。

これを満たさない場合、親は `BlockedByMissingSliceImplDelegation` として停止し、成功扱いしてはいけません。

`slice-impl` に渡す入力は、少なくとも次です。

- parent Plan
- parent triage output
- parent slice decomposition artifact
- assigned slice artifact
- per-slice change-risk-triage
- per-slice implementation-contract-kernel（必要な場合）
- per-slice implementation-contract-review-kernel（存在する場合）
- per-slice runtime-contract-kernel
- per-slice test-design-kernel
- parent review gate の implementation authorization
- bounded parent Plan pass / Guardrail Focus coverage / non-goals / stop condition

`slice-impl` は次を行います。

1. `implementation-handoff-review` を実行する。
2. READY でない場合は実装せず停止する。
3. 親が承認した assigned slice-local bounded parent Plan pass を実装する。Guardrail Focus artifacts は deep-check guardrail として扱い、implementation scope として扱わない。
4. 無関係な refactoring や redesign を行わない。
5. required checks を実行する。実行できない check は理由を明記する。
6. slice-local `verification-kernel` を実行する。
7. slice-local verification-kernel の verdict（例: `PARENT_PLAN_VERIFIED`、`PARENT_PLAN_NEEDS_RESIDUAL_DECISION`、`PARENT_PLAN_PARTIAL_WITH_FIX_CANDIDATES`、`BLOCKED_*`）と Remaining Work / residual candidates を出力して停止する。

`slice-impl` は次を行ってはいけません。

- parent review gate が承認した bounded parent Plan pass を広げる
- cross-slice-verification-kernel を実行する
- `XC-xxx` を単独で完了扱いにする
- gap を見つけた場で coverage-gap-resolution へ進む
- さらに subagent を起動する

### slice-impl の出力形式

```md
# Slice Implementation Result: SL-xxx

## Verdict

- Status: PARENT_PLAN_VERIFIED / PARENT_PLAN_NEEDS_RESIDUAL_DECISION / PARENT_PLAN_PARTIAL_WITH_FIX_CANDIDATES / BLOCKED_*
- Reason:

## Agent metadata

- Agent type: slice-impl
- Model:
- Reasoning effort:
- Parent authorization artifact:
- Delegation evidence:

## Changed files

## Covered IDs

| ID | Kind | Status | Notes |
| --- | --- | --- | --- |

## Checks run

## Checks not run

## Production binding evidence

## Remaining Work

## Handoff to parent
```

## Agent Usage Ledger

親エージェントは `plans/<ticket-or-slug>-agent-usage-ledger.md` を必須成果物として作成・更新してください。

```md
# Agent Usage Ledger

## Execution mode

- Mode: PREP_ONLY / DELEGATED_IMPLEMENTATION / PARENT_DIRECT_IMPLEMENTATION_EXCEPTION
- Parent model:
- Parent direct code edit allowed: Yes / No
- Reason if exception:
- Explicit human approval if exception:

## Expected delegation

| Phase | Slice | Delegation required | Expected agent type | Expected tier/model | Edit owner | Parallel group |
| --- | --- | --- | --- | --- | --- | --- |

## Observed agent runs

| Run ID | Phase | Slice | Agent name | Agent type | Model | Reasoning effort | Edit allowed | Artifact | Outcome |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |

## Delegation compliance

| Rule | Status | Evidence |
| --- | --- | --- |
| All executable slices passed slice-prep or were blocked | PASS / FAIL | |
| All READY slices were implemented by slice-impl | PASS / FAIL | |
| Parent did not edit production code/tests | PASS / FAIL | |
| Cross-slice verification was run by parent | PASS / FAIL | |
```

## Final gate: cross-slice-verification-kernel and residual-decision-gate

すべての実装対象 slice の verification-kernel 結果が出そろった後、親エージェントは `cross-slice-verification-kernel.agent.md` を実行し、その後に unresolved items を `residual-decision-gate.agent.md` へ渡します。

確認対象は次です。

- parent Plan
- parent triage output
- slice decomposition artifact
- 各 slice artifact
- 各 slice-prep artifact
- 各 slice-impl result
- 各 verification-kernel result

cross-slice verification では次を確認してください。

- parent acceptance conditions が slice 分割後も満たされているか
- `XC-xxx` の producer / consumer / mechanism / required fields / state / identifiers が一致しているか
- field continuity が upstream source から downstream consumer まで traceable か
- production wiring / entrypoint / DI / config / migration / persistence が slice 間でつながっているか
- stub-only success や production binding gap が残っていないか
- Remaining Work が parent PASS を妨げるものかどうか分類されているか

cross-slice verification では、見つけた gap をその場で修正しません。必要なら `coverage-gap-triage.agent.md` に渡すための handoff を作成し、residual candidate は `residual-decision-gate.agent.md` で explicit human decision の有無を判定して停止します。

## 並列化ルール

並列化してよいのは、次を満たす場合だけです。

- parent review gate が `Can implement now? = Yes` と判定している
- shared ownership risk が低い
- 同じ production wiring / public API / schema / migration / durable state を編集しない
- producer slice の output が consumer slice の実装前提になっていない
- `XC-xxx` に unresolved field / state / identifier が残っていない
- 失敗時に単独で rollback / discard できる

次の場合は直列化してください。

- 同じ files / modules / schema / public API を触る
- producer / consumer の片方だけを実装すると false PASS になり得る
- `NeedsHumanDecision` / `Deferred` が downstream behavior に影響する
- implementation-realization risk の解消結果で downstream design が変わる
- migration / state transition / retry / recovery semantics が絡む

## Codex への短い指示例

準備までで止める場合:

```text
$token-aware-full-coverage-3layer を使って、この full-coverage decomposition を Plan網羅チェック・残件判定フローとして進めて。
slice-prep で各 slice の準備 artifact を作り、parent review gate までで停止して。
実装はまだ行わない。
```

実装と cross-slice verification まで進める場合:

```text
$token-aware-full-coverage-3layer を使って進めて。
ExecutionMode は DELEGATED_IMPLEMENTATION とし、
parent review gate で READY になった slice は必ず slice-impl に渡し、
各 slice の verification-kernel 後に cross-slice-verification-kernel と residual-decision-gate まで実行して。
gap があれば修正せず coverage-gap-triage または residual-decision-gate への handoff を作って停止して。
```

## 最終監査

親エージェントは完了前に次を確認してください。

- ExecutionMode が `Agent Usage Ledger` に記録されている
- `DELEGATED_IMPLEMENTATION` の場合、親が production code / tests を直接編集していない
- `plan-slice-decomposition` から直接実装していない
- slice-prep と parent review gate を通している
- READY でない slice を実装していない
- `Can implement now? = Yes` の slice はすべて `slice-impl` に渡されている
- `slice-impl` run が存在しない READY slice は `BlockedByMissingSliceImplDelegation` として停止している
- `Agent Usage Ledger` が作成・更新され、`DelegationCompliance` が PASS / FAIL / EXCEPTION_ACCEPTED で判定されている
- `PARENT_DIRECT_IMPLEMENTATION_EXCEPTION` は明示理由とユーザー承認がある場合だけ使われ、3層委譲成功としてカウントされていない
- cross-slice contract を slice 内で完了扱いにしていない
- verification-kernel で gap 修正に進んでいない
- cross-slice-verification-kernel と residual-decision-gate を最後に実行している、または未実行理由を明示している
