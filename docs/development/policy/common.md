# HVO engineering policy — 2026-11-17.2

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

Enforcement changes are changes to mechanisms that gate, block, admit or merge work, or change
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
toolchain. It may not relax common procedure; procedural additions are limited to
the authorities below. A profile may have a clearly labeled “Transitional retained procedures” list. Each listed item remains in force until Roy records a decision to retire it or promote it to common, or until the next policy release, whichever comes first; it may only be stricter, never relax common policy. "Next policy release" means the next shared-policy version bump. A profile may retain qualification and evidence-translation requirements as standing profile text, including items recorded at this bump, provided they do not relax common procedure. A profile may also set a stricter retry budget as described in [Validation and runtime routes](#validation-and-runtime-routes). The repository adoption profile describes local skill discovery.
No global installation is a prerequisite; no adapter grants broader scope or
waives gates. Stop and resolve conflicts rather than guessing. An assignment may narrow the permitted
scope or add requirements; it cannot lower review-tier minima, required distinct-model
independence, required validation, protections or safety boundaries. An explicit
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
implementation, PR-ready, merged or deployed). Kepler may merge autonomously only
with explicit authorization: this standing rule supplies that authorization, subject
to any assignment-specific limit and all required gates. An assignment may narrow
its endpoint to draft-only or PR-ready. After required CLEAN reviews on the exact
head, required independence and coverage, and resolution of eligible review threads,
Kepler continues without a further manual signal from Roy: publish any remaining review
and verification records, then mark ready, wait for qualified CI and the profile’s
review-evidence gate on the non-draft head, apply the ready-to-merge label if
used, and merge by the profile method bound to the exact reviewed head SHA. Never use
admin rights or bypass actors. CHANGES_REQUIRED, INCOMPLETE or BLOCKED review,
unresolved blocking threads, failed or unqualified CI, or a decision only Roy can make
stops the merge. Ask Roy for release publication, deployment (including staging),
production changes, reboots, deletions (including production, user data, repositories
or machines), physical or hardware operations, waivers, protection or ruleset
changes, unresolved policy conflicts and other decisions only he can make. Physical
operations include operating the real roof or any other physical device. Release,
deployment and physical operations each require their own explicit authorization.
Use `Closes #n` in the PR for the issue it completes. A partial slice closes its own
child issue with `Closes #child` and references its parent with `Refs #parent`, leaving the parent open.
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
   Requalify a moving merge source before merge, verifying source, review, checks
   and resolved findings.
7. **Merged → Verified complete:** observe actual merge SHA/target and required
   post-merge results, then verify acceptance and closure. Queue admission is not
   completion. Complete ordinary closeout after checking active bindings; reboots and deletions
   require Roy’s decision. Report deployment obligations separately.

Blocked, paused, canceled, failed and ambiguous are explicit non-complete states.
An uncertain remote mutation must be reconciled before any replay.

## Independent review contract

| Risk | Minimum review |
|---|---|
| LOW: former Low, including non-executable documentation/presentation without behavior or security impact | One independent frontier model with a CLEAN verdict; Sol is the default |
| HIGH: former Standard and High, including ordinary behavior/API changes, safety, security, durability, state/migrations, concurrency/recovery, deployment, cross-component effects, camera lifecycle, CI trust, policy text or policy enforcement | Two distinct independent frontier models, both CLEAN on the same head, at least one Deep; Sol + Opus is the default pairing |

The tier mapping is the accepted interim conservative rule (Roy, 2026-11-17,
4:16 AM PST, widget t584s3285): former Low maps to LOW, and former Standard and
High map to HIGH. When unsure, choose HIGH. Only Roy changes this mapping. Sol
means provider-reported `gpt-6.1-sol`; Opus means provider-reported
`claude-opus-5-5`. Other distinct frontier models, including a DeepSeek model,
are allowed when identified by their exact provider-reported model ID.
Independence means distinct frontier models regardless of provider; provider does
not determine independence. Qualify by exact provider-reported model ID, not an
alias, display name or provider name. Never infer identity or independence from
any name, alias or provider.

A model must never review work it authored or contributed to in the reviewed range.
The author and other contributors must not be any required reviewer model for that
PR; a contributor match does not independently satisfy that review role. If a
required reviewer model is unavailable, another distinct frontier model that did
not author or contribute may fill the role. HIGH still requires two distinct models
and at least one Deep review. If no such independent model is available, the role
is BLOCKED. A coordinator, reviewer, implementer or any other agent must never
waive this requirement. The only exception is Roy’s own manual decision described
below. A reviewer must not author or modify the candidate or
finalize it during that review. Record requested and provider-reported model and
effort separately, session, scope, base/head/target, coverage, checks and limitations.
Unknown provenance stays unknown and cannot satisfy a strict model requirement.

Reported model IDs and distinct-model independence are reported, not authenticated.
Automated HIGH admission means an automated ready, CI or merge decision for HIGH
work. When the author and required reviewers are distinct reported models, requested
and provider-reported identity is sufficient for that admission, with the unauthenticated
identity limit recorded; no additional per-head Roy acceptance is required. Merge
authorization remains separate under the standing rule. The only exception to the
independence requirement is Roy's own manual decision addressing a disclosed
independence or contributor-overlap defect for one exact full head SHA. An agent
can never waive the requirement or grant this exception. Roy’s decision must be
recorded in GitHub PR metadata bound to that full SHA, disclosing that model
identity is reported, not authenticated, and naming each reviewer's requested and
provider-reported model; it may also be recorded in history after merge. The decision
addresses only the disclosed defect for that head and waives no other requirement,
including coverage, checks, local evidence, merge method or the ban on admin bypass.
It is not authenticated proof of distinct models. A decision for one head never
carries over to a later head; each new head needs its own reviews and, if needed,
its own recorded decision. The review minimum
is checked at Ready and before merge, not before implementation, commit, push or
draft PR creation.

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

Never use live tests as ordinary validation. Before any protection or ruleset change,
qualify in a fixture, capture authenticated baseline and impact evidence, and verify
a rollback plan; stop for an unknown baseline or concurrent change.

Preserve profile-required tests, security checks, coverage floors and architecture
boundaries. Run focused checks during work and the mandatory local preflight before
review; protected-branch checks must qualify the actual candidate before merge.
Use established host resource gates for heavy jobs; cancel only obsolete owned jobs.
Do not weaken checks or tests to obtain green. CI/queue optimization is separate.

Retain failed evidence. Deterministic failures require correction. As procedure, the default
unchanged-source infrastructure budget is two retries; a profile may set a stricter
budget. This budget is not claimed to be implemented. Harness, provider instance,
effort and fallback routes remain assignment configuration, subject to the review
contract's independent model requirements. Validate capability and
supported effort in the actual launch context. Record requested and
observed settings and fallback reason. A catalog entry is not proof of execution.
For transient provider failures, use bounded backoff with jitter and provider hints
(starting proposal: 5/15/45 seconds), then only an authorized compatible fallback
that preserves required review separation/capability. Diagnose auth/configuration
or policy denials; do not bypass them. Uncertain execution needs reconciliation,
not a retry timer. Missing mandatory routes leave work blocked.
