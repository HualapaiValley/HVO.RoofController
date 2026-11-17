# RoofController adoption profile

Policy version: `2026-11-16.11`. This preparation and the first adoption stop at
PR-ready. Roy separately authorizes undraft, `hvo/reviewed-head`, and merge.
The canonical portable source is identified in the [manifest](manifest.json).
Target `main`; there is no merge queue. Use a merge commit and a direct merge
pinned with `--match-head-commit` after the reviewed-head gate.

## Scope and discovery

The [common policy](common.md#scope) defines designated pilot work, policy text,
and enforcement changes. Policy text includes `AGENTS.md`, `CLAUDE.md`,
`CONTRIBUTING.md`, `.github/copilot-instructions.md`, `.agents/skills/**` and
`docs/development/policy/**` where they carry normative agent workflow rules.
Enforcement includes `.github/workflows/**`, branch protection, rulesets and
required checks. Policy text and enforcement changes are High risk.

Load [common policy](common.md), root [AGENTS.md](../../../AGENTS.md), applicable
repository instructions, and the local [coordination skill](../../../.agents/skills/hvo-coordination/SKILL.md)
for pilot work. The [adapters](adapters.md) describe harness discovery; automatic
discovery is not verified. `CLAUDE.md` and Copilot instructions route to root
AGENTS.md. No Cursor discovery adapter is qualified here.

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

The draft checks before review are the applicable draft CI workflows on the exact
head, including `ci.yml` and `scenarios.yml`, and the image workflows when their
inputs change, as required by the existing lifecycle. PR-triggered workflows can
run on drafts without `hvo/reviewed-head`. Review records are published before
Ready and final CI; after review converges and all applicable workflows pass on
the reviewed head, Kepler publishes `hvo/reviewed-head` on that exact head before
an authorized direct merge. It is a manual commit status, not an Actions check,
CI admission gate, or merge-queue entry gate. Branch protection requires it for
merge. The existing lifecycle also requires applicable workflows that protection
does not require.

## Validation and boundaries

Run `build/secret-scan.py` from the repository root and require `PASS` before
every commit, push and GitHub post; scan body files as specified in
[AGENTS.md](../../../AGENTS.md#secrets). `src/global.json` pins SDK `10.0.403`
with `rollForward: patch`; run `dotnet` from `src/`. Do not use .NET 11 for this
adoption. Preserve the repository's hardware-free validation and safety boundaries:
never move the real roof as ordinary work. The existing AGENTS.md lifecycle,
review vocabulary and safety rules remain in force subject to common policy.

## Evidence translation

RoofController review verdicts are `CLEAN` and `CHANGES_REQUIRED`. Common `CLEAN`
maps to RoofController `CLEAN` only when coverage is complete; it does not grant
merge. `BLOCKED` and `INCOMPLETE` are non-approval, with the limitation recorded;
they must not be represented as `CLEAN`. The common reviewer minima apply, including
the High-risk Sol and Opus panel and deep review, as does RoofController's existing
rule that an agent reviewer use a different model from the author. Common policy
also requires separation from every contributor to the reviewed range.

## Transitional retained procedures

These remain in force until Roy records a decision to retire or promote them, or
until the next shared-policy version bump, whichever comes first; they only add
requirements to common policy.

- The existing lifecycle requires applicable draft CI to pass before requesting
  independent review, and all applicable workflows to pass before the manual
  reviewed-head status, including workflows not required by branch protection.
- Existing review format, finding disposition and hardware-free safety procedures
  in [AGENTS.md](../../../AGENTS.md) remain required where stricter than common.
