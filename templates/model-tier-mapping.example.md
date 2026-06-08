# Model Tier Mapping Example

This file is an example for maintainers.
Do not treat these labels as fixed model names.

| Tier | Real model | Notes |
| --- | --- | --- |
| `HIGH_MODEL` | `gpt-5.5` | Use for hard judgment and high-risk planning. Start with high/xhigh reasoning. |
| `STANDARD_MODEL` | `gpt-5.5` | Use for normal implementation and verification. Start with medium reasoning. This default is `same-model-lower-effort`; confirm that effort-only cost reduction is acceptable or replace with a lower-cost model. |
| `CHEAP_MODEL` | `gpt-5.4-mini` | Use for read-heavy scan, docs consistency, and simple local fixes. Start with low reasoning. |

Review this mapping when contracts, available models, pricing, or quality requirements change.
The matching runnable examples live in `profiles/codex-first/agents/*.toml`.

## Mapping review checklist

- `HIGH_MODEL` is reserved for hard planning, risk, implementation contract, and dangerous close judgment.
- `STANDARD_MODEL` is lower cost than `HIGH_MODEL`, or explicitly accepted as `same-model-lower-effort`.
- `CHEAP_MODEL` is suitable for read-heavy scan, docs consistency, artifact format check, and simple local fixes.
- READY implementation delegates to `standard-implementer`.
- READY verification delegates to `standard-verifier`.
