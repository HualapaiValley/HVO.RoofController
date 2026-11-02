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

## The managed heap reading and the command history

C14 failed its managed heap check three times between October 27 and November 1, 2026: the nightly on `main` at
`08c3121` (+31.7%, trend +22.0%) and at `c94d5ad` (+21.4%, trend +9.8%), and PR #232's two-hour soak at `d1fd160`
([run 41349362192](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/41349362192): +32.7%, trend
+17.9%). Two causes, neither in the controller, account for them (#220, PR #232).

**The reading.** The check read `GC.GetTotalMemory(forceFullCollection: true)`, which forces a full collection and
then returns the runtime's approximation of the bytes in use. `soak-memory.jsonl` also records the GC's own figures
for that collection: every sample's record is a full blocking collection (generation 2, not concurrent; 177 of them
did not compact), and its heap size less its fragmentation is the bytes it left live. Over 2,266 samples from the 22
nightly and pull request soaks listed below (October 19 to November 1), the approximation read more than 0.1 MB under
the live bytes in 976 samples, by up to 8.2 MB, and never more than 0.1 MB over (at most 77 KB). Whether the first or
last five samples land on short readings decides the endpoint. On `08c3121` the live bytes moved +0.6%
while the reading said +31.7%. The controlled experiment above shows the same: identical baselines read −20.67% and
+0.68%.

The counted set is the 14 `main` nightlies, #159's `c81ff60` run, the six #220 phase-3 runs (PR #230), and phase 4's
`d1fd160` run (PR #232). Counts use each artifact's `soak-memory.jsonl` once, without its duplicate under `TestResults`.
"Short" means the approximation is more than 100,000 bytes below `gc.heapSizeBytes - gc.fragmentedBytes`.

| Source | Run | Samples | Short readings | Non-compacting collections |
|---|---|---:|---:|---:|
| `main`, October 19 | [39219392805](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/39219392805) | 111 | 96 | 0 |
| `main`, October 20 | [39388842958](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/39388842958) | 111 | 73 | 0 |
| `main`, October 21 | [39564928863](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/39564928863) | 111 | 34 | 0 |
| `main`, October 22 | [39742930447](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/39742930447) | 110 | 64 | 0 |
| `main`, October 23 | [39919994367](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/39919994367) | 111 | 63 | 0 |
| `main`, October 24 | [40074264822](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/40074264822) | 111 | 111 | 0 |
| `main`, October 25 | [40173735698](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/40173735698) | 111 | 17 | 10 |
| `main`, October 26 | [40288979059](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/40288979059) | 111 | 31 | 25 |
| `main`, October 27 | [40471150266](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/40471150266) | 111 | 51 | 0 |
| `main`, October 28 | [40659120915](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/40659120915) | 111 | 26 | 12 |
| `main`, October 29 | [40844028053](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/40844028053) | 111 | 37 | 21 |
| `main`, October 30 | [41028937194](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/41028937194) | 111 | 47 | 24 |
| `main`, October 31 | [41185254524](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/41185254524) | 111 | 56 | 0 |
| `main`, November 1 | [41293163036](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/41293163036) | 111 | 40 | 0 |
| #159, `c81ff60` | [39836178067](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/39836178067) | 111 | 80 | 0 |
| Phase 3, `caa8ae5` | [41246784183](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/41246784183) | 56 | 9 | 0 |
| Phase 3, `5601293` (cancelled) | [41263313041](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/41263313041) | 1 | 0 | 0 |
| Phase 3, `5601293` | [41263441072](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/41263441072) | 109 | 20 | 6 |
| Phase 3, `04348ca` | [41271582342](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/41271582342) | 109 | 24 | 24 |
| Phase 3, `6eda002` | [41289321486](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/41289321486) | 109 | 16 | 42 |
| Phase 3, `0e1295b` | [41298177163](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/41298177163) | 109 | 30 | 13 |
| Phase 4, `d1fd160` | [41349362192](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/41349362192) | 109 | 51 | 0 |
| **Total (22 soaks)** | | **2,266** | **976** | **177** |

The 30-minute phase-3 run (`caa8ae5`) and the cancelled run contribute to the reading counts, but not to the 20
completed two-hour soaks compared below. The six #159 controlled-experiment variants in
[run 39817647033](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/39817647033) are excluded from this
22-soak set. They add 666 samples, 357 short readings and 50 non-compacting collections: including them gives **28
soaks, 2,932 samples, 1,333 short readings and 227 non-compacting collections**. The stated maximum shortfall and
overshoot, generation and concurrency claims also hold over all 28.

**The command history.** The roof command history (#206) keeps every command in memory, up to
`RoofCommandHistory:MaximumEntries` (10,000 by default) or its retention. A soak cycle records four commands, so the
history grows for about six hours of soak before it fills: about 0.4 to 0.5 KB a command, which is the step between
these groups of runs:

| Runs, judged on the live bytes with C14's own formula | Growth after the warm-up | Trend |
|---|---:|---:|
| 13 two-hour soaks before the history (`main` and #159's branch, October 19-30) | +0.3% to +0.6% | +0.4% to +0.6% |
| 7 two-hour soaks with it (`main` from `2dbdbee`, the #220 phase 3 and phase 4 branches) | +4.7% to +5.4% | +4.9% to +5.7% |

The same runs judged on the approximation range from −20.8% to +32.7%. Every one passes on the live bytes; phase 4
reads +5.3% (trend +5.5%), as the others with the history do.

Heap snapshots attribute the step. `dotnet-gcdump` snapshots were taken of 24-minute C14 runs on `main` at `25a23f4`
and on PR #232 at `d1fd160`, side by side on one machine, at minutes 7, 15 and 23. Between the last two, each added 254
`RoofCommandHistoryEntry` objects (four per cycle) and their strings. Neither added anything else that grows with the
cycles but the emulator's event log, which was still filling to its 1,000-event limit, and the harness's own samples.
No Alpaca command state (tickets, grants, alerts, leases), timer or subscription grew. The history is bounded: at its
default limit it holds about 4 to 5 MB.

**The correction.** C14 judges the live bytes the forced collection left, read from
`GC.GetGCMemoryInfo(GCKind.FullBlocking)`. A reading whose collection is not newer than the one before is refused.
`GetTotalMemory` still forces the collection, and its approximation is recorded as `totalMemoryBytes`.
`C14ManagedHeapTests` replays run 41349362192's settled samples through the check: they fail it as the approximation
(+32.7%) and pass it as the live bytes (+5.3%). A leak of 6% added to the live bytes still fails it. The soak also
keeps the command history at its least, 100 entries, so it is full 25 cycles in, as the emulator's event log is during
the warm-up (`C14SoakRigScenarios`). The warm-up, sample cadence, endpoint medians, least-squares trend and the 10%
limits are unchanged.
