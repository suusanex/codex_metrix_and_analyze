# Stop Report

task_slug:
state_artifact:
current_gate:
next_gate:
recommended_model_tier:
execution_mode:
allowed_to_edit:
edit_owner:
delegation_required:
expected_agent:
configured_model:
configured_reasoning_effort:
hook_model: unknown
reported_model:
effective_model: unknown
parent_direct_work_reason:
delegation_violation: No
cost_saving_delegation_countable: No
delegation_compliance:
stop_reason:

## Why Stopped


## Minimum Human Input Needed


## Not Allowed Until Resolved

- Implementation outside READY scope
- Parent-direct execution of a delegated gate without ParentDirectExecutionException and explicit human approval
- Counting parent-direct work or trivial parent fixes as cost-saving delegation
- Close when DelegationCompliance is FAIL or Agent Usage Ledger is missing
- Close / completion claim
- Secret, external service, billing, or production operation

## Delegation Evidence Needed

- Expected agent:
- Missing observed run:
- Required artifact:
- Required configured model / reasoning evidence:
- Required hook / reported / effective model distinction:

## Resume Command

```text
続きやって。
```
