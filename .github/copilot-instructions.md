# Copilot instructions: HVO.RoofController

The instructions for every coding agent, Copilot included, are in [AGENTS.md](../AGENTS.md) at the repository root,
and the controller's own in [src/HVO.RoofControllerV4.RPi/AGENTS.md](../src/HVO.RoofControllerV4.RPi/AGENTS.md). Read
them before making a change; they are the only copy, so this file does not repeat them.

The rules that come first, from AGENTS.md:

- Never move the real roof; motion is tested against the emulated HAT, roof and VFD, and no test relies on hardware.
- The repository is public: never commit, print, log or post a secret, and run `build/secret-scan.py` before every
  commit, push, issue, pull request and comment.
- Keep the working VFD setup and wiring; offer improvements as optional recommendations.
- Publishing a release is a person's job.
