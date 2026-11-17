# HVO engineering policy — 2026-11-16.11

The [manifest](manifest.json) identifies the canonical source and pins the portable
files; distribution is an explicit reviewed copy with digest verification, never
a runtime fetch or a global symlink. A future common-source repository must be
designated before distributing further snapshots. Edit the canonical source named
in the manifest, update the manifest,
and review the complete snapshot together. A digest proves consistency, not trust.

This implements the accepted decisions in HVO-policy-proposal.md, Library version
1, design v0.2 (16 November 2026), pinned by identity and byte digest in the
[manifest](manifest.json). The source remains in Library; access is required to
inspect it, and a digest alone cannot replace that inspection. The review contract
below supersedes earlier model-selection guidance.
The repository adoption profile fixes the pilot stages. The manifest names that
profile, and root AGENTS.md links it. Existing trusted evaluators remain authoritative;
these documents neither grant assignment authority nor publish a passing gate.

## Scope

Pilot work means assignments explicitly designated as pilot work by Roy in the
assignment, and reviews of those assignments; it does not mean all work on this
host or repository. Policy text means normative agent workflow instructions and
adapters where they carry workflow rules; the repository adoption profile lists
the concrete paths. Product or user
documentation that merely mentions a policy is not policy text.

Enforcement changes are changes that gate, block, admit or merge work, or change
branch protection, rulesets, required checks, review-evidence/admission logic or
CI gating, wherever they live. The repository adoption profile lists the concrete
paths and settings.

## Authority and precedence

Platform/security requirements come first, then this common policy, then the
repository adoption profile. All repositories follow this common policy. The six
folded rules on reviewer model separation, bypass, review timing, named tiers,
PR operations and closing keywords apply to all agent work, including non-pilot
work. Except for these six rules, this policy governs pilot work and policy text
or enforcement changes as defined in Scope; other work follows the repository's
existing procedures, described by the repository adoption profile, subject to the
six rules. A
profile lists only structural differences: branch, queue or merge route, required
merge gate and who sets it (trusted automation, Kepler or Roy), required checks, required pre-post checks such as a
secret scan, and SDK pin location, plus repository facts such as paths and
toolchain. It may not relax or add procedural rules except for a clearly labeled
“Transitional retained procedures” list. Each listed item remains in force until Roy records a decision to retire it or promote it to common, or until the next policy release, whichever comes first; it may only be stricter, never relax common policy. "Next policy release" means the next shared-policy version bump. The repository adoption profile describes local skill discovery.
No global installation is a prerequisite; no adapter grants broader scope or
waives gates. Stop and resolve conflicts rather than guessing. An assignment may narrow the permitted
scope or add requirements; it cannot lower review-tier minima, required model-family
separation, required validation, protections or safety boundaries. An explicit
assignment is not a waiver. Resolve conflicts before the affected action.
Never bypass branch protection, rulesets or required checks using admin rights or
bypass actors. No identity, including Max or Kepler, has special privileges.
Kepler's assigned operations are duties, not privileges, and grant no authority to
waive or lower any gate. Among agents, Kepler, the
coordinator, alone opens PRs (including drafts), marks them ready or returns them
to draft, publishes review and verification records, records final merge readiness
or the reviewed-head status, and enqueues or merges when authorized. Review and
verification records are published before Ready and CI; after qualified CI and
final checks, Kepler records final merge readiness or reviewed-head status before
an authorized merge. The merge gate encompasses these distinct stages; it does not
delay review-record publication until CI. Coding and reviewer threads do
not perform these operations. Roy may act directly. Kepler follows every rule,
cannot waive or lower any gate, and its changes receive the same review as any
other changes. A coordinator is required for these agent PR operations. Kepler
publishes reviewer output verbatim, links the reviewer session, and never writes or
changes verdicts or findings; verification records are faithfully relayed too.

Record owner, scope, acceptance criteria, risk and endpoint (research,
implementation, PR-ready, merged or deployed). Kepler may merge autonomously
only with explicit authorization and after all required review and check gates
pass; Roy may act directly. A draft-only assignment never merges. Use `Closes #n`
in the PR for the issue it completes. A partial slice closes its own child issue
with `Closes #child` and references its parent with `Refs #parent`, leaving the
parent open.
Release publication, deployment and physical operations each require their own
explicit authorization, independent of merge authority and of each other. Physical
operations include operating the real roof or any other physical device.
Full-access, noninteractive execution is allowed only where supported and authorized;
it never overrides platform restrictions or authorizes credential/network bypass.

## Lifecycle and evidence

1. **Intake:** verify canonical remote, target/base SHA, clean or owned working
   changes, policy/profile version, existing PRs and ownership. Map each acceptance
   criterion to evidence; classify by impact rather than line count.
2. **Claimed:** establish one accountable owner and durable work key. Use
   each worker’s own isolated, owned git worktree, never a shared or main checkout;
   record its path. Fetch and verify the assigned base SHA before implementation.
   Stale activity is not permission to take ownership.
3. **Implementing:** make the smallest coherent change; preserve unrelated work.
   Reproduce faults when feasible and test relevant behavior/failure boundaries.
   Record actual commands, source, performer, results and unavailable checks.
4. **Independent review:** begin only after the draft checks named by the repository
   adoption profile have passed on the exact head (latest run). Reviewers
   check the exact candidate head and base..head
   range read-only, stopping if the head differs; inspect requirements, affected
   interactions and all carried findings. Correct blockers and obtain a
   fresh verdict for the exact new head. An incomplete review is not approval.
5. **Ready:** finalizer verifies ownership, current target/head, acceptance, local
   validation, review independence/count and independently verified dispositions.
   After three unsuccessful correction cycles, replan/escalate; never waive blockers.
6. **CI/merge queue:** use the profile's existing checks and merge method. Bind
   evidence to the current head/target and tested merge candidate. Before another
   push, reconcile queue/auto-merge state, return to draft as appropriate and renew
   affected evidence. Kepler performs the agent draft transition. Never bypass branch
   protection, rulesets or required checks with admin rights or bypass actors.
7. **Merged → Verified complete:** observe actual merge SHA/target and required
   post-merge results, then verify acceptance and closure. Queue admission is not
   completion. Clean up only owned, recorded, provably merged resources after
   checking active bindings. Report deployment obligations separately.

Blocked, paused, canceled, failed and ambiguous are explicit non-complete states.
An uncertain remote mutation must be reconciled before any replay.

## Independent review contract

| Risk | Minimum review |
|---|---|
| LOW: former Low, including non-executable documentation/presentation without behavior or security impact | One independent Sol reviewer with a CLEAN verdict |
| HIGH: former Standard and High, including ordinary behavior/API changes, safety, security, durability, state/migrations, concurrency/recovery, deployment, cross-component effects, camera lifecycle, CI trust, policy text or policy enforcement | Independent Sol and Opus reviewers with CLEAN verdicts on the same head; at least one deep review |

The tier mapping above is the accepted interim conservative rule (Roy, 2026-11-17,
4:16 AM PST, widget t584s3285): former Low maps to LOW, and former Standard and
High map to HIGH. When unsure, choose HIGH. Sol means provider-reported model ID
`gpt-6.1-sol`; Opus means provider-reported model ID `claude-opus-5-5`. Qualify
by the exact reported ID, not an alias. Only Roy changes this mapping.
Every reviewer must use a different model from the author and every contributor
to the reviewed range. Authors and other contributors to the reviewed range must
not use Sol or Opus. If a contributor's model equals a required reviewer model,
that review role is BLOCKED. Arrange a new candidate with a nonconflicting
contributor set; no decision may waive the current separation requirement. If Sol or Opus is
unavailable for a required role, that role is BLOCKED, not waived.

A reviewer must not author/modify the candidate or finalize it during that review.
Never infer model family from a name or alias. Record requested and provider-reported
model/effort separately, session, scope, base/head/target, coverage, checks and
limitations; unknown provenance remains unknown.

The review minimum, including Sol and Opus CLEAN reviews from different reported
model families and at least one deep review for HIGH risk, is checked at Ready and before any
merge decision; it does not block
implementing, committing, pushing or opening a draft PR. Automated High-risk
admission means an automated ready/CI/merge decision that relies on model-family
separation; Sol and Opus families are reported, not authenticated. Until family
can be authenticated, Roy decides and records the
decision using honestly labeled reviews. If two reported reviews are accepted for bootstrapping the
High-risk two-family minimum while families cannot be authenticated, Roy makes and
records that manual decision, disclosing family uncertainty and each reviewer's
requested and provider-reported model. Never describe it as authenticated proof
of two families. The decision covers only disclosed model-family uncertainty and
waives no other requirement: coverage, independence, required checks, local
evidence, the PR-ready stop and merge authorization remain required. Acceptance
for one head never carries over: each new head needs its own reviews and, if needed,
its own recorded decision.

Distinct thread IDs are observable;
distinct environment sessions may not be. A high-risk
finalizer should be distinct from the implementer where practical.

Verdicts are CLEAN, CHANGES_REQUIRED, INCOMPLETE or BLOCKED. Findings have stable
IDs, severity, evidence, impact, possible correction and verification criteria.
Acceptance, correctness, safety, security, data-loss, compatibility and required-test
failures block regardless of priority. Independently verify dispositions; eligible
nonblocking improvements may become documented follow-ups. Never omit carried
findings to obtain a clean verdict. Repository evidence adapters translate formats
without relaxing this contract.

## Validation and runtime routes

Preserve profile-required tests, security checks, coverage floors and architecture
boundaries. Run focused checks during work and the mandatory local preflight before
review; protected-branch checks must qualify the actual candidate before merge.
Use established host resource gates for heavy jobs; cancel only obsolete owned jobs.
Do not weaken checks or tests to obtain green. CI/queue optimization is separate.

Retain failed evidence. Deterministic failures require correction. As procedure, the default
unchanged-source infrastructure budget is two retries; a stricter transitional retained procedure takes precedence until Roy records a decision to retire it or promote it, or until the next policy release, whichever comes first.
This budget is not claimed to be implemented.
Sol and Opus are required reviewer models as specified above. Harness, provider
instance, effort and any fallback route remain assignment configuration; a fallback
may not substitute a different required reviewer model. Validate capability and
supported effort in the actual launch context. Record requested and
observed settings and fallback reason. A catalog entry is not proof of execution.
For transient provider failures, use bounded backoff with jitter and provider hints
(starting proposal: 5/15/45 seconds), then only an authorized compatible fallback
that preserves required review separation/capability. Diagnose auth/configuration
or policy denials; do not bypass them. Uncertain execution needs reconciliation,
not a retry timer. Missing mandatory routes leave work blocked.
