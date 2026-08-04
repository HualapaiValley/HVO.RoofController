# Architecture Review - August 2026

## Scope

This review covers the Raspberry Pi control service, iPad client, hardware abstractions, concurrency and shutdown behavior, HTTP API, error handling, health checks, logging, deployment, and dependency/tooling health.

## Validation Baseline

- SDK: .NET `10.0.302`
- Portable build: succeeds with zero warnings and errors
- RPi tests: 67 passed
- iOS: validated by the macOS 26 / Xcode 26.6 GitHub Actions workflow
- Stable package updates: none available from configured sources
- Deprecated packages: none
- Known advisory: transitive `Microsoft.OpenApi 2.0.0` is affected by `GHSA-v5pm-xwqc-g5wc`; the advisory is narrowly suppressed because `Microsoft.AspNetCore.OpenApi 10.0.10` currently fixes that transitive version

The full solution cannot be built on Linux because `net10.0-ios` requires the Apple toolchain. Linux validation must build the RPi test project, which covers the server, common library, theme library, and tests.

## Completed During Review

- Production and Compose defaults now enable physical limit switches.
- Periodic fallback verification now de-energizes all relays when it detects a terminal limit or contradictory limit state.
- Docker health checks now use hardware-aware readiness.
- Docker logs are bounded to five 10 MB files in both deployment paths.
- Unexpected HTTP 500 responses no longer expose exception messages and include trace correlation fields.
- Linux build documentation now uses the portable project graph.

## Priority Findings

### P0 - Safety and Control

1. **Relay write failures can still be reported as successful commands.**
   `SetRelayStatesAtomically` logs failures but does not return a failure-bearing result. Open, close, stop, and shutdown can therefore claim success without a verified safe relay state.

2. **Safety-input read failures fail open.**
   Failed I2C reads retain stale or null values; null limits are interpreted as normal and null fault state as inactive. Initialization and movement must reject unverifiable safety inputs and force a stop/error state.

3. **Movement endpoints are unauthenticated state-changing GET requests.**
   Any network client, crawler, or link prefetch can actuate the roof. Commands should be authenticated POST requests with operator authorization and replay protection.

4. **The documented dead-man behavior is not implemented as a renewable control lease.**
   The existing watchdog is a maximum movement timer. Client loss does not stop motion until that long timeout or a limit. Define and implement a short renewable operation lease if continuous operator presence is a requirement.

### P1 - Concurrency and Shutdown

1. `UpdateConfiguration` can synchronously wait for a periodic task while holding the lock that task needs, creating a deadlock.
2. Disposal can race a new movement command before the disposed state is published.
3. A queued callback from an old watchdog generation can stop a newer operation.
4. `ClearFaultAsync` does not serialize the complete relay pulse, ignores write failures, and needs bounded duration plus `finally` cleanup.
5. Status events create untracked `Task.Run` work, can be delivered out of order, and silently swallow subscriber exceptions.
6. Shutdown paths ignore stop failures and can log a safe stop without verifying relay state.

### P2 - Reliability and Client Behavior

1. iPad command retries have no explicit short timeout and may replay ambiguous movement operations.
2. iPad configuration writes are not atomic and can leave a truncated settings file.
3. Expected safety interlocks map to HTTP 500 instead of stable conflict/unavailable error codes.
4. Detailed health and the live log viewer require an administrator access policy before the controller is exposed beyond a trusted isolated LAN.

## Observability Plan

### Stage 1 - Local durability and correlation

- Keep the bounded live UI log buffer.
- Use bounded Docker log rotation (completed).
- Emit one structured startup event containing application version, boot/session ID, hardware mode, effective safety settings, environment, and assembly versions.
- Include trace IDs in API errors (completed for handled exceptions).
- Log failed shutdown stop attempts at critical severity.

### Stage 2 - Operational metrics

Add low-cardinality counters and gauges for:

- command attempts and outcomes
- safety stops by reason
- controller state
- hardware read/write failures
- watchdog expirations
- initialization retries
- readiness state

Use OpenTelemetry metrics with OTLP export to a collector hosted off the Pi. Telemetry export must never block or delay a roof command.

### Stage 3 - Tracing and iPad diagnostics

- Instrument ASP.NET Core and `HttpClient` tracing.
- Propagate W3C trace context from iPad operations.
- Add rotating local iPad diagnostic logs and managed crash/lifecycle records.
- Provide an explicit diagnostics export workflow with redaction.

## Required Validation for Follow-up PRs

- Inject relay write and safety-read failures and assert command failure, error state, zero relay mask, and unhealthy readiness.
- Race configuration update, disposal, watchdog expiry, and movement commands under deterministic synchronization.
- Verify anonymous movement commands are denied and GET commands return 405.
- Verify readiness becomes unhealthy for initialization, hardware, and controller errors.
- Verify telemetry collector outages do not affect command latency or safety behavior.
- Run a 24-hour Pi soak test with bounded disk growth, stable memory, and no missed safety transitions.
