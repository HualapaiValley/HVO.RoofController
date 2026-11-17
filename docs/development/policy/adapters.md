# Thin harness and Kepler adapters

Load the [common policy scope and rules](common.md#scope), the repository adoption profile (named by the [manifest](manifest.json) and linked from root AGENTS.md),
AGENTS.md and applicable local skills; automatic discovery is not verified.

| Harness | Discovery contract |
|---|---|
| Codex | Read root AGENTS.md and its explicit local skill paths; do not assume a global skill with the same name is equivalent. |
| Claude | Verify the repository adoption profile’s instruction routing and local skill-link resolution; explicitly load its canonical local skills. Same-named or auto-surfaced global `hvo-*` skills are not equivalent and do not supersede these local contracts. |
| OpenCode | Follow the repository adoption profile’s instruction and wrapper routing to canonical procedures; model aliases are not execution evidence. |
| Copilot | Follow the repository adoption profile’s instruction routing to AGENTS.md and local skills. |
| Cursor | Check the repository adoption profile for installed or qualified Cursor rules/discovery adapters. Explicitly load AGENTS.md and its linked local contracts; verify effective loading before use. |
| T3 | T3 hosts an underlying harness; identify it and follow its row, explicitly loading AGENTS.md and local contracts. Record both T3 and underlying harness identities; neither T3 nor this table proves automatic discovery. |

At launch, record harness/version, actual host/workspace/session, policy digest,
assignment boundary, requested route/effort, observable actual values and effective
permission/noninteractive mode. Verify the mode in a harmless local qualification;
do not infer it from configuration labels. Unknown provenance does not block launching work but cannot satisfy a strict
model requirement; that review role stays INCOMPLETE or BLOCKED as specified in
the [review contract](common.md#independent-review-contract). Roy's manual
bootstrapping decision covers family uncertainty only, not unknown model identity.
Do not invent command flags or elevate access to satisfy a policy.
Required reviewer models are fixed by common policy; other preferences and
authorized fallbacks belong in assignment configuration.

## Kepler coordination

Kepler follows the [coordination procedure](../../../.agents/skills/hvo-coordination/SKILL.md)
using the T3 hub MCP for pilot work. Record requested and provider-reported settings separately;
missing provenance stays unknown. A harness or alias does not prove model family
or distinct environment sessions. Missing required capability blocks the affected
operation.
