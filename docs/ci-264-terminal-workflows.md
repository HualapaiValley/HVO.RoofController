# Terminal workflow timeout evidence (Refs #264)

The failing process is the entire installer contract catalogue: 1,011 tests, excluding
render, Scenario, Browser, Soak and the opt-in host installation suites. It has one
600-second wall-clock budget. CLI contracts and renders finish before it starts.
This is reproducible on unchanged `main` at
`6e044ef18fb63d07884a955659cd9c043ede3d9c`, independently of Refs #255 / PR #259.

Read-only Actions logs establish the following failures. Every row ends with Python's
`TimeoutExpired` for the installer contracts at 600 seconds. Step duration includes
the earlier CLI phases and staging; it is not the installer process's duration.

| Run | Job | Event | Whole workflow-exercise step |
| --- | --- | --- | --- |
| [42354627755](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/42354627755) | 128351463895 | main schedule, Nov 7 | 866 s |
| [42463147445](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/42463147445) | 128678126199 | main schedule, Nov 8 | 769 s |
| [42589991644](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/42589991644) | 129071818445 | main schedule, Nov 9 | 886 s |
| [42788076162](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/42788076162) | 129737471338 | main schedule, Nov 10 | 896 s |
| [42977458463](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/42977458463/job/130382331099) | 130382331099 | PR #259 | 918 s |

All five failures use image `macos-26-arm64`, version `20261102.0473.1`, macOS
26.7.2 (25G332). The same main commit passed scheduled runs
[41997972151](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/41997972151)
and [42190870047](https://github.com/HualapaiValley/HVO.RoofController/actions/runs/42190870047)
on Nov 5–6. The Nov 6 job used image `20261026.0455.2`, macOS 26.6.2 (25G83).
The image transition is a runner-condition correlation; the retained evidence does
not establish an OS-level cause or rule out an individual stalled test.

The uploaded `terminal-runtime-*` artifacts' TRX files measure the passing processes:

| Host/run | Installer contracts passed | Process elapsed (TRX start to finish) | Sum of test durations |
| --- | --- | --- | --- |
| macOS arm64, Nov 5 / 41997972151 | 1,011 | 376.11 s | 1,118.82 s |
| macOS arm64, Nov 6 / 42190870047 | 1,011 | 337.90 s | 1,004.94 s |
| Linux x64, Nov 10 / 42788076162 | 1,011 | 25.59 s | 90.48 s |

Tests run concurrently, so their summed durations are not process elapsed time.
On Nov 6, `InstallerLifecycleTests` (78 tests), `InstallerBackupTests` (100), and
`InstallerStateContractTests` (97) consumed 176.22, 175.13 and 172.76 summed
seconds respectively: 524.11 seconds, over half the catalogue's total. Each group
occupied about 60–61 seconds of that process's wall time; the remaining contracts
occupied approximately 158 seconds. The slowest individual test was
`Renew_WhileTheRoofMoves_PutsTheCertificateInPlace_AndRedeploysOnlyOnceItIsIdle`
at 7.52 seconds. These are fake-machine workflows, with real local file and
certificate operations, rather than waits for a controller or real installation.

Both passing macOS baselines matter. The 515 CLI contract rows common to all seven
artifacts show substantial variation, including between the two passing runs:

| macOS run | Sum of the 515 common CLI test durations | `RoofCliCaCertificateTests` (33 tests) |
| --- | --- | --- |
| Nov 5 / 41997972151, installer passed | 183.91 s | 24.85 s |
| Nov 6 / 42190870047, installer passed | 100.63 s | 12.39 s |
| Nov 7 / 42354627755, installer timed out | 200.57 s | 60.19 s |
| Nov 8 / 42463147445, installer timed out | 122.93 s | 58.97 s |
| Nov 9 / 42589991644, installer timed out | 189.24 s | 57.31 s |
| Nov 10 / 42788076162, installer timed out | 220.15 s | 59.11 s |
| PR #259 / 42977458463, installer timed out | 210.06 s | 77.16 s |

Rows are identified by class, method and display name. Two pairs of grant cases
share class/display-name keys; omitting the method collapses the 515 cases into 513
keys and gives the earlier Nov 5/Nov 10 totals of 183.46/219.64 seconds. The table
above retains every common case.

The Nov 5-to-Nov 10 comparison is much smaller than the Nov 6-to-PR comparison.
The certificate class slows disproportionately; some other groups are faster than
on Nov 5 (for example, `RoofCliRoofCommandTests`: 38.33 s on Nov 5, 33.14 s in the
PR run). These observations do not support a uniform runner-slowdown multiplier or
an extrapolation of CLI timing to the installer budget. The timeout's cause remains
**UNPROVEN**: certificate costs, another installer operation, accumulated resource
use or a stalled test cannot be distinguished without installer progress evidence.

The correction partitions those three groups from a complement containing every
remaining contract, including future classes. Each batch retains its own 600-second
limit; render filters, command isolation, required contract assertions and exact
bundled-assembly staging remain in force. This removes the single budget shared by
all 1,011 tests without enlarging the per-process limit. The whole workflow-exercise
step is bounded at 20 minutes within the unchanged 30-minute job, with evidence upload
still configured for `always()`. Batching is a mitigation; it does not establish the
timeout's cause or certify that a hung test is repaired.
Console logging now saves each completed test's duration directly to
`workflow-run.txt`, including on failure or timeout. Exception cleanup kills the
whole VSTest process group, including on SIGINT or SIGTERM. Previously, capture was
discarded when the timeout raised, and the failed PR artifact contained no installer
TRX or progress log.

macOS confirmation must come from CI on a PR, which Kepler handles. A successful
macOS job on the corrected main-equivalent branch, and on PR #259 or its successor,
remain required evidence for the issue's first two acceptance criteria. Linux
validation cannot establish those results. No workflow was dispatched, retried or
cancelled for this investigation, and no issue comment was posted; this document
is the record available for Kepler's later issue update.
