# Codex First State

task_slug:
original_user_intent:
source_of_truth:

current_gate:
next_gate:
recommended_model_tier:
execution_mode: ROUTE_ONLY / DELEGATED_WORK / PARENT_DIRECT_WORK / TRIVIAL_PARENT_FIX
selected_agent_name:
selected_agent_type:
configured_model:
configured_reasoning_effort:
hook_model: unknown
reported_model:
effective_model: unknown
allowed_to_edit: false
current_status:
stop_reason:
parent_direct_work_reason:
delegation_violation: No
cost_saving_delegation_countable: No

allowed_stop_reasons:
- DelegationRequired
- NeedsHumanDecision
- ManualVerificationRequired
- NeedsHigherModelReview
- NeedsSecretInput
- NeedsExternalOperation
- Blocked
- TooCostlyForCurrentPass
- ReadyButAwaitingHumanApproval
- DelegationUnavailable
- DelegationEvidenceMissing
- ParentDirectExecutionException
- ParentDirectExecutionNotAllowed
- RoutingPolicyViolation
- BlockedByMissingDelegationLedger
- ReadyForDelegatedImplementation
- ReadyForDelegatedVerification

## Routing Plan

| Gate | Recommended tier | Delegation required | Expected agent type | Edit owner | Parent may execute directly? | Stop if unavailable |
| --- | --- | --- | --- | --- | --- | --- |

## Execution Mode

- execution_mode: ROUTE_ONLY / DELEGATED_WORK / PARENT_DIRECT_WORK / TRIVIAL_PARENT_FIX
- ROUTE_ONLY: intake / plan / risk / contract / close judgment only; no production code or test edits.
- DELEGATED_WORK: selected agent / subagent owns the bounded work; parent updates state, ledger, and close decision.
- PARENT_DIRECT_WORK: parent works without agent / subagent delegation; reason required and not cost-saving delegation.
- TRIVIAL_PARENT_FIX: explicit low-risk local fix only; not cost-saving delegation.

## Edit Permission

- allowed_to_edit: Yes / No
- edit_owner: parent / standard-implementer / standard-verifier / high-planner / high-implementation-contract / high-risk-triage / high-closure-reviewer / cheap-repo-scanner / cheap-doc-consistency / cheap-artifact-format-checker / human / none
- parent_direct_edit_allowed: Yes / No
- allowed_paths:
- forbidden_paths:
- required_authorization_artifact:

human_required_items:
- None

artifacts_created:
- None

artifacts_consumed:
- None

unresolved_residuals:
- None

operations_not_allowed_in_current_state:
- Do not implement before READY.
- Do not parent-direct execute a gate with DelegationRequired = Yes unless ParentDirectExecutionException has explicit human approval.
- Do not mark implementation complete without observed standard-implementer run or accepted exception.
- Do not mark verification complete without observed standard-verifier run or accepted exception.
- Do not perform secret, external service, billing, or production operations without explicit approval.
- Do not close with unresolved ManualVerificationRequired, NeedsHumanDecision, or NeedsHigherModelReview.
- Do not close with DelegationCompliance = FAIL or missing Agent Usage Ledger.

## Agent Usage Ledger

### Expected delegation

| Gate | Delegation required | Expected agent | Expected tier | Edit owner | Reason |
| --- | --- | --- | --- | --- | --- |

### Observed runs

| Run ID | Gate | Work item | Model tier | Agent name | Agent type | Configured model | Configured reasoning effort | Hook model | Reported model | Effective model | Delegation required | Edit owner | Delegation violation | Cost-saving delegation countable | Outcome | Evidence |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |

### Model field meanings

- model_tier: abstract routing label, one of HIGH_MODEL / STANDARD_MODEL / CHEAP_MODEL.
- configured_model: Codex custom agent file top-level `model`.
- configured_reasoning_effort: Codex custom agent file top-level `model_reasoning_effort`.
- hook_model: model observed from hook payload or hook log, otherwise unknown.
- reported_model: model self-reported by the agent; lower confidence than configured_model or hook_model.
- effective_model: billing or runtime-effective model only when independently verified, otherwise unknown.

### Delegation compliance

| Check | Status | Evidence |
| --- | --- | --- |
| CHEAP work delegated when required | PASS / FAIL / N/A | |
| STANDARD implementation delegated | PASS / FAIL / N/A | |
| STANDARD verification delegated | PASS / FAIL / N/A | |
| Parent direct execution exception documented | PASS / FAIL / N/A | |
| Delegation violation absent or accepted | PASS / FAIL / N/A | |
| Cost-saving delegation has observed delegated run evidence | PASS / FAIL / N/A | |

delegation_compliance: PASS / FAIL / EXCEPTION_ACCEPTED / N/A

next_action:
last_updated_summary:
