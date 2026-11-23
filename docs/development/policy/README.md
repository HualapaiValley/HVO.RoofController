# Policy snapshot maintenance

Start with [common policy](common.md) and the repository adoption profile. The
[manifest](manifest.json) names that profile, identifies the candidate version and
canonical source, and lists the portable files. Root AGENTS.md links the profile.
The layout consists of the portable files plus one repository-specific profile and
manifest. The repository adoption profile lists only structural differences:
branch, queue or merge route, required merge gate and who sets it (trusted automation,
Kepler or Roy), required checks,
required pre-post checks such as a secret scan, and SDK pin location, plus facts
such as paths and toolchain. It may not relax common procedure; procedural additions are limited to the
transitional retained procedures, standing qualification and evidence-translation
requirements, and stricter retry budgets described in common policy. From the
repository root, verify the pinned portable files with:

```sh
shasum -a 256 -c docs/development/policy/snapshot.sha256
```

On an intentional policy change, update the version string in manifest.json
(`version` and `policyVersion`), the common.md title, the repository adoption
profile and AGENTS.md. Keep scope, precedence and review-timing summaries and
local-skill statements in repository instruction files, and the scope link in
adapters.md, consistent with common.md.
Regenerate snapshot.sha256 for exactly the manifest's file list; review both content and digest changes.
On distribution, copy the portable set byte-identically, with a repository-specific
profile and manifest, and compare portable version/digests against the reviewed
canonical commit. Do not regenerate hashes to hide an unexplained mismatch. This
local consistency check is not independent review, protected-branch enforcement or
a live adoption test. Existing repository skills and requirements are pinned by
the repository revision and are not portable common-policy files.
