---
name: hvo-coordination
description: Coordination procedure for agent PR operations in all work, with pilot-specific dispatch routing.
---

# Coordination procedure

Read [common policy](../../../docs/development/policy/common.md) and the
repository adoption profile (named by the manifest and linked from root AGENTS.md).
One accountable coordinator owns each work domain. A standalone worker may own implementation;
Kepler performs agent PR operations under the common policy.
Record the assignment, owner, scope, risk, endpoint, source, evidence and state.
Do not take over ambiguous ownership or treat silence as failure. Each worker uses
its own isolated, owned git worktree, never a shared or main checkout; record the
path. Fetch and verify the assigned base SHA before implementation. Review the
exact candidate head and base..head range read-only; stop if the head differs.

For pilot work, dispatch and observation go through the T3 hub MCP. The repository
adoption profile identifies the existing route for non-pilot epic work. The pilot
designation selects the route; never dispatch the same work through
both. Before launch record a unique `clientRequestId` and title
nonce, along with project/environment, time window and prompt digest. Pass the
request ID and nonce at launch. Record the returned thread ID and other handles.
Keep these bindings in the coordinator’s own written dispatch record for the assignment.
Poll with `t3_thread_read`; never call `task_status` as a read.

Treat any uncertain launch, send, push or merge as AMBIGUOUS. Reconcile before
any retry against all available bindings: clientRequestId, thread ID,
environment/project, time window, prompt digest and title nonce. A relaunch with the same clientRequestId is a retry, not reconciliation. A single title
match is not proof. A failed, partial or paginated lookup is not proof of absence.
If the outcome cannot be positively established from all available bindings, it
remains AMBIGUOUS; report to the human instead of retrying. Never relaunch blindly. A later run on a completed thread
requires fresh evidence review.

A read-only watchdog is separate from recovery that can resume work. Until a recovery procedure is tested,
recovery stops at a report to the human. Record requested and provider-reported
model and effort separately; unknown provenance stays unknown. Distinct thread IDs
are observable, but distinct environment sessions may not be. Follow the common
review and authorization rules before finalization.
