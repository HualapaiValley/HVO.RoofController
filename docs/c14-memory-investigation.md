# C14 working-set investigation (#159)

The intermittent working-set failures are consistent with delayed tiered compilation growing glibc arena residency
in the combined test process. The controlled experiment below removes nearly all of that growth by either disabling
tiering or limiting allocator arenas. This supports an explicit **test-harness measurement correction**: run the C14
workflow's test step with `MALLOC_ARENA_MAX=2`, keeping tiered compilation and all existing criteria.

This does not establish the absence of native leaks, identify the owner of every allocation, or measure the deployed
controller's default allocator configuration. No production setting changes. The scope is the controller, exporter,
emulated plant/camera and harness together. No component-specific leak was confirmed, so none was removed or replaced
to obtain a passing result. A future failure still requires investigation, including component isolation if indicated.

## Existing nightly evidence

Ten two-hour nightlies (October 13–22, 2026) show growth concentrated before roughly minute 75. Endpoint growth after
the original 30-minute warm-up ranges from +2.6% to +11.1%; the final 45 minutes range from −1.5% to +1.5%. The later
window is diagnostic only: it is **not** the assessment window. The failing October 15 and October 18 runs retain
their original failures. Earlier runs lack the diagnostics added in #163, so their allocation ownership is unknown.

| Run | Original working-set endpoint growth | Diagnostic growth from minute 75 |
|---|---:|---:|
| [38377439922](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/38377439922) | +6.7% | −1.5% |
| [38530915388](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/38530915388) | +7.8% | +0.6% |
| [38699849285](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/38699849285) | +11.1% | +1.1% |
| [38866809670](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/38866809670) | +8.0% | +0.5% |
| [39012260615](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/39012260615) | +7.8% | +1.2% |
| [39107226369](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/39107226369) | +10.0% | +1.5% |
| [39219392805](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/39219392805) | +6.0% | +0.0% |
| [39388842958](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/39388842958) | +6.7% | +1.2% |
| [39564928863](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/39564928863) | +6.6% | +0.7% |
| [39742930447](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/39742930447) | +2.6% | +0.3% |

Those values use CSV timestamps rounded to 0.1 s. The controlled experiment's calculations below use exact JSONL
timestamps. The four instrumented nightlies show growing anonymous residency and continued JIT compilation, without
comparable managed-heap growth. GC commitment alone cannot identify resident managed pages or exclude a native leak.

## Controlled two-hour experiment

[Run 39817647033](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/39817647033) ran six independent
GitHub-hosted Linux x64 jobs on commit `1719b416a5ca461c34eba757f20305eaa06d1a6e`, on October 22, 2026. Each used the
same Debug build source, SDK 10.0.402, executing .NET 10.0.13, four processors and server GC. Each retained the
production controller settings, black-hole OTLP collector, status polling, camera streams and emulated cycling.
Each job built first, then ran just C14 for `02:00:00`, with the original 30-minute warm-up.

The temporary workflow matrix selected two replicates each of: the default environment; only
`DOTNET_TieredCompilation=0`; only `MALLOC_ARENA_MAX=2`. The workflow logs record those settings. These runs predate
the schema-2 environment recorder, so their environment files do not contain `runtimeSettings`. They are concurrent
jobs on different hosted machines, not same-machine paired trials; two replicates do not quantify long-term failure
probability. The temporary matrix and dispatch-only job exclusions are removed from the final workflow.

| Variant | Working set, first → last five medians (MiB) | Endpoint | Trend | Managed endpoint / trend | JIT methods added, settled period |
|---|---:|---:|---:|---:|---:|
| baseline-a | 205.44 → 222.77 | +8.43% | +7.80% | −20.67% / +2.35% | 1,556 |
| baseline-b | 213.79 → 228.68 | +6.97% | +5.51% | +0.68% / +6.67% | 1,552 |
| tiering-off-a | 178.55 → 173.48 | −2.84% | −2.50% | +0.35% / −6.18% | 1 |
| tiering-off-b | 176.77 → 175.38 | −0.79% | −1.68% | −20.87% / −14.57% | 2 |
| arena2-a | 188.98 → 192.83 | +2.04% | +1.92% | −5.21% / −5.19% | 1,564 |
| arena2-b | 191.94 → 193.84 | +0.99% | +1.93% | +0.50% / +9.93% | 1,525 |

All six passed all 13 original checks. Each completed 873–874 cycles, 174 mid-travel Stops and 1,746–1,748 measured
Stops; the slowest Stop per run was 154–171 ms. No Stop-latency drift, fault latch, Error/Critical entry, plant
invariant violation, failed status poll or status-version regression occurred. Every sampled input/relay read was
healthy and fresh, all camera streams closed, and the exporter continued trying the unresponsive collector. Thread
and descriptor endpoint medians stayed within their original bounds. The complete invariant details, resource
series and input fingerprints are retained in [the generated report](c14-runtime-variants-2026-10-22.json).

Working-set medians in successive 15-minute buckets, MiB (the final sample just past two hours is excluded here):

| Variant | 0–15 | 15–30 | 30–45 | 45–60 | 60–75 | 75–90 | 90–105 | 105–120 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| baseline-a | 194.3 | 198.3 | 208.7 | 211.5 | 219.2 | 220.1 | 221.1 | 221.2 |
| baseline-b | 197.6 | 205.6 | 217.7 | 219.3 | 224.8 | 225.0 | 225.2 | 225.6 |
| tiering-off-a | 176.9 | 173.8 | 178.6 | 175.7 | 173.4 | 173.4 | 174.1 | 173.3 |
| tiering-off-b | 178.1 | 174.7 | 176.8 | 177.6 | 177.2 | 175.2 | 175.6 | 175.7 |
| arena2-a | 189.2 | 187.1 | 189.5 | 189.6 | 191.5 | 191.8 | 193.1 | 192.3 |
| arena2-b | 193.3 | 191.2 | 191.7 | 191.1 | 195.7 | 198.1 | 194.0 | 194.3 |

## What the mapping snapshots establish

The first settled and final snapshots show 31 non-main **arena-shaped heaps** in both baseline runs; their aggregate
RSS grows by 15.32 and 19.44 MiB. With two arenas requested there is one such non-main heap, growing by 1.70 and
3.00 MiB. With tiering disabled those heaps change by −0.14 and +0.27 MiB. The main `[heap]` is reported separately.
The arena2 runs still add about as many JIT methods as baseline; runtime doublemapper RSS grows about 1.4 MiB in
both baseline and arena2 snapshots. This distinguishes allocator-backed residency from generated-code mapping growth.

The shape classifier recognizes a 64 MiB aligned anonymous `rw-p` span with an adjacent `---p` remainder ending at
the next 64 MiB boundary (or a fully writable span). This follows the normal non-main heap layout in
[glibc's arena implementation](https://github.com/bminor/glibc/blob/glibc-2.42/malloc/arena.c). It is a shape inference,
not a malloc allocation trace, and does not classify every anonymous mapping. It does not identify a GC reservation
by its size. Individual snapshots differ from endpoint medians; GC/proc observations are sequential, not atomic.

[.NET's compilation documentation](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/compilation)
describes background optimized compilation after initial code generation.
[GNU's allocator documentation](https://sourceware.org/glibc/manual/latest/html_node/Memory-Allocation-Tunables.html)
describes an arena-count maximum whose default on 64-bit systems depends on processor count. Taken together with
the independent interventions, the evidence supports tiering-associated allocator retention as the dominant
instrumented growth mechanism. A plateau and flat managed heap alone would not support that attribution. Live
native bytes, fragmentation and freed memory retained by glibc remain indistinguishable without allocation traces.
The uninstrumented historical failures cannot be retrospectively assigned this cause with certainty.

## Correction and reproduction

`MALLOC_ARENA_MAX=2` is set before the workflow starts the C14 test process. It limits arena count, not total live
allocation size, and allows freed blocks to be reused by more threads. Fewer arenas can increase malloc contention;
the two controlled runs retain the original Stop-latency, thread/descriptor and safety checks. The setting applies
only to this combined-process test harness. It does not validate memory residency or malloc contention under the
deployed controller's default allocator. Other scenario, browser, kiosk and image tests retain their environment.

The C14 scenario, 30-minute warm-up, sample cadence, forced collection, endpoint medians, least-squares trend,
strict 10% thresholds, managed-memory check and every safety/Stop invariant remain byte-for-byte unchanged. No
`malloc_trim`, RSS subtraction, later assessment window, component removal or larger limit is used. This environment
correction must be assessed explicitly in independent review before finalization.

Schema 2 of `soak-environment.json` records requested, allowlisted runtime/allocator variables, with only numeric
values published. Missing names mean unset; invalid values are withheld. These are requested settings, not proof
of their effective runtime precedence. No arbitrary environment values, arguments or hostnames are recorded.

Download the six named `soak-results-<variant>-39817647033` artifacts from the experiment, then reproduce the
committed report from the repository root (substitute the downloaded directory for `artifacts`):

```bash
python3 build/analyze-c14-memory.py \
  baseline-a=artifacts/soak-results-baseline-a-39817647033/soak-results \
  baseline-b=artifacts/soak-results-baseline-b-39817647033/soak-results \
  tiering-off-a=artifacts/soak-results-tiering-off-a-39817647033/soak-results \
  tiering-off-b=artifacts/soak-results-tiering-off-b-39817647033/soak-results \
  arena2-a=artifacts/soak-results-arena2-a-39817647033/soak-results \
  arena2-b=artifacts/soak-results-arena2-b-39817647033/soak-results
```

The JSON preserves all original check details, exact resource calculations, 15-minute managed/committed/anonymous
and working-set series, mapping-category residency and SHA-256 fingerprints of the inputs. Raw JSONL, CSV, sanitized
smaps, environment and TRX files remain in the run artifacts for their retention period. The offline report is
diagnostic, never a substitute for the original C14 gate.

For a full local verification, build and run other heavy checks first, then from `src/` run C14 alone on the emulated
plant. Serialize heavy work on a shared worker with `flock -o /tmp/hvo-heavy-gate.lock timeout 150m`, unset
`HVO_PRODUCTION_CATALOG_BUNDLE`, set `DOCKER_HOST=unix:///tmp/hvo-no-docker.sock`, and use:

```bash
MALLOC_ARENA_MAX=2 HVO_SOAK_DURATION=02:00:00 dotnet test \
  ../tests/HVO.RoofControllerV4.RPi.Tests/HVO.RoofControllerV4.RPi.Tests.csproj \
  --no-build --filter 'FullyQualifiedName~C14SoakScenarios' \
  -nodeReuse:false -p:UseSharedCompilation=false
```
