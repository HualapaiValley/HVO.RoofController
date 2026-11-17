# RoofController adoption profile

Policy version: `2026-11-16.11`. The designated canonical portable source is
HualapaiValley/HVO.WebSite `main`, reviewed at commit
`ae2f2b75240838135b98da37440e5c42efaa5da1` and authorized for this adoption by
`t586s3317`; see the [manifest](manifest.json).
Target `main`; there is no merge queue. Use a merge commit and a direct merge
pinned with `--match-head-commit` after the reviewed-head gate.

## Pilot stages

This preparation and first adoption stop at PR-ready. Later stages require
separate authorization from Roy for undraft, `hvo/reviewed-head`, and merge.

## Scope and discovery

The [common policy](common.md#scope) defines designated pilot work, policy text,
and enforcement changes. Policy text includes `AGENTS.md`, `CLAUDE.md`,
`CONTRIBUTING.md`, `src/HVO.RoofControllerV4.RPi/AGENTS.md`,
`src/HVO.RoofControllerV4.RPi/CLAUDE.md`, `.github/PULL_REQUEST_TEMPLATE.md`,
`.github/copilot-instructions.md`, `docs/releasing.md`, `.agents/skills/**` and
`docs/development/policy/**` where they carry normative agent workflow rules.
Enforcement includes `.github/workflows/**`, branch protection, rulesets, merge
methods, required checks, `build/secret-scan.py`, `build/check-base-images.sh`
and settings controlling review evidence or CI admission.
Policy text and enforcement changes are High risk.

Load [common policy](common.md), root [AGENTS.md](../../../AGENTS.md), applicable
repository instructions, and the local [coordination skill](../../../.agents/skills/hvo-coordination/SKILL.md)
for pilot work. The [adapters](adapters.md) describe harness discovery; automatic
discovery is not verified. `CLAUDE.md` and Copilot instructions route to root
AGENTS.md. Claude has a `.claude/settings.json` SessionStart setup hook, not a
`.claude/skills` link; explicitly load the canonical local skill. No OpenCode
wrapper or dedicated instruction adapter is present here: load AGENTS.md and its
linked local procedures explicitly. No Cursor discovery adapter is qualified here.
There is no dedicated non-pilot epic dispatch route in this repository; such
assignments follow the existing AGENTS.md working-in-git and pull request lifecycle
procedures, subject to common policy's six rules.

Existing contributor conventions in AGENTS.md and CONTRIBUTING.md remain in
force, including branch and commit naming, CHANGELOG and documentation in the
same PR, merging main into a branch, never force-pushing, coding standards and
pinning workflow actions.

## Agent pull request operations

Among agents, only Kepler opens PRs (including drafts), marks them ready or returns
them to draft, publishes review records, records reviewed-head, and merges; Roy may
do these directly. Coding and reviewer threads stay read-only for those PR operations
and do not post. Kepler relays reviewer output word for word. The rest of the
AGENTS.md lifecycle remains in force as specified here. For agent PRs, common's
`Closes #n` rule supersedes the `Fixes #<n>` wording in the repository template,
AGENTS.md and CONTRIBUTING.md; a partial slice uses `Closes #child` and
`Refs #parent`.

## Protection, draft checks and merge gate

The observed `main` protection is classic branch protection with non-admins
enforced (`enforce_admins` is false). No ruleset targets `main`; the active
`release tags` ruleset targets `refs/tags/v*`. Re-query these settings before
future work. The 14 required contexts are:

- `build-and-test`, `deploy-script`, `secret-scan`, `install-sh-mac`,
  `installer-kiosk`, `installer-mac`, `mac-app`, `mac-cli`;
- `scenarios`, `browser`, `container-scenarios`, `installer-rig (ubuntu-latest)`,
  `installer-rig (ubuntu-24.04-arm)`;
- `hvo/reviewed-head`.

The draft checks before review are the latest runs of each applicable workflow on
the exact head, including `ci.yml` and `scenarios.yml`, and the image workflows
when their inputs change. They must finish successfully before review. PR-triggered
workflows can run on drafts without `hvo/reviewed-head`; marking Ready starts no
workflow (the pull_request default event types apply, and branch protection is not
strict). Common's CI step after Ready is the AGENTS.md lifecycle step 6.2 re-check
of the latest applicable runs on the reviewed head and actual merge candidate,
not a requirement for a new run by itself. If main has moved since the qualifying
draft runs, that re-check must qualify the current candidate rather than silently
reuse runs against an older merge base.
Review records are published before Ready and that re-check; after review converges
and all applicable workflows pass on the reviewed head, Kepler publishes
`hvo/reviewed-head` on that exact head before an authorized direct merge. For
High-risk work, when the author and reviewers are distinct reported models, no
additional Roy per-head acceptance of those reviews is required before Ready,
reviewed-head or merge. Requested and provider-reported model identity is enough;
the review record must say that identity is reported, not authenticated, and must
not describe it as authenticated proof of family or of two families. If the author
or any contributor and a reviewer are not distinct reported models, or a required
identity is missing, that review role is BLOCKED or INCOMPLETE per common.md and
adapters.md. No decision waives that requirement under version `2026-11-16.11`;
arrange a nonconflicting reviewer or candidate. Merge authorization remains separate.
Roy's decision `t589s3353` supersedes common's per-head acceptance requirement for
RoofController when the author and reviewers are distinct reported models. This is a
recorded owner exception to common.md's rule that a profile may only be stricter and
to common's per-head acceptance text. It ends when RoofController adopts a shared
version that incorporates it, tracked by HualapaiValley/HVO.WebSite pull request #499 (issue #498),
or when Roy retires it, whichever comes first. The status
is a manual commit status, not an Actions check, CI admission gate, or merge-queue
entry gate. Branch protection
requires it for merge. The existing lifecycle also requires applicable workflows
that protection does not require.

## Validation and boundaries

Run `build/secret-scan.py` from the repository root and require `PASS` before
every commit, push and GitHub post; scan body files as specified in
[AGENTS.md](../../../AGENTS.md#secrets). `src/global.json` pins SDK `10.0.403`
with `rollForward: patch`, keeping SDK selection within the 10.0.4xx band; .NET 11
is not used. Run `dotnet` from `src/`. Tests and checks use the
emulator, as required by AGENTS.md.

The [Never move the real roof](../../../AGENTS.md#rules-that-come-first) rule
applies in full: no agent sends Open or Close to a controller driving the real HAT
unless a person has said, for that task, that no roof or drive is connected to it;
nothing an agent writes, including a test, script or deploy step, may do it either.
Reading status and sending Stop are fine. The installer never moves the roof.
Common policy's explicit physical-operation authorization does not lift any part
of this boundary, and no assignment may lower it. The other AGENTS.md safety rules
remain in force.

AGENTS.md's release rules remain in force: common's explicit release-publication
authorization does not let an agent publish a release, change a package's
visibility, or delete or move a published tag.

## Evidence translation

RoofController review verdicts are `CLEAN` and `CHANGES_REQUIRED`. Common `CLEAN`
maps to RoofController `CLEAN` only when coverage is complete; it does not grant
merge. `BLOCKED` and `INCOMPLETE` are non-approval and are published as given,
with the blocker or limitation, never as `CLEAN` or `CHANGES_REQUIRED`. RoofController
F-numbers and severities may be added alongside a reviewer's own IDs and P0–P3
priorities, never substituted for them. The severity mapping is P0 → Critical,
P1 → High, P2 → Medium and P3 → Low. Retain stable IDs, blocking disposition
and per-finding reproduction as required by AGENTS.md. The common reviewer minima apply, including
the High-risk Sol and Opus panel and deep review, as does RoofController's existing
rule that an agent reviewer use a different model from the author. Common policy
also requires separation from every contributor to the reviewed range. When the author
and reviewers are distinct reported models, Roy's `t589s3353` decision supersedes
common's per-head acceptance requirement for RoofController: no additional Roy
per-head acceptance of those reviews is required before Ready, reviewed-head or
merge. Requested and provider-reported model identity is enough; the review record
must say that identity is reported, not authenticated, and must not describe it as
authenticated proof of family or of two families. If the author or any contributor
and a reviewer are not distinct reported models, or a required identity is missing,
that review role is BLOCKED or INCOMPLETE per common.md and adapters.md. No decision
waives that requirement under version `2026-11-16.11`; arrange a nonconflicting
reviewer or candidate. Merge authorization remains separate. This is a recorded
owner exception to common.md's rule that a profile may only be stricter and to
common's per-head acceptance text. It ends when RoofController adopts a shared
version that incorporates it, tracked by HualapaiValley/HVO.WebSite pull request #499 (issue #498),
or when Roy retires it, whichever comes first.
