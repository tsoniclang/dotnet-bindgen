# Strict Mode Performance Baseline

Captured: 2025-11-22 11:33:39 UTC

## Environment

- Machine: diablo
- OS: Linux 6.14.0-35-generic
- .NET: 10.0.100
- BCL: Microsoft.NETCore.App/10.0.0-rc.1.25451.107

## Baseline Metrics

### Wall Time
- **Total generation time**: 22.022356032s

### Phase Timings
- **SCC pass**: N/Ams
- **Honest emission planning**: N/Ams

### Output Statistics
- **Namespaces**: 130
- **Types**: 4295
- **Members**: 50720

## Notes

This baseline was captured on the `feat/strict-hardening-determinism` branch after:
- PR D (zero warnings achievement)
- Follow-up PR tasks A-C (determinism, strict policy cleanup, info hygiene)

The baseline serves as a reference point for detecting significant performance regressions.
No CI gating is implemented yet - this is for visibility only.

## Interpretation

- Total wall time includes all phases: reflection, aggregation, transform, planning, validation, emission
- SCC pass time measures strongly-connected-component analysis for circular dependency handling
- Honest emission planning time measures interface conformance analysis and unsatisfiable interface detection

**Expected variance**: ±10-20% depending on system load and disk I/O
**Regression threshold**: >2x slowdown warrants investigation
