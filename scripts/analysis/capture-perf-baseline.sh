#!/bin/bash
# Capture performance baseline for strict mode generation

set -euo pipefail

echo "================================================"
echo "Performance Baseline Capture"
echo "================================================"
echo ""

# Clean output directory
rm -rf .tests/perf-baseline
mkdir -p .tests/perf-baseline

# Capture start time
start_time=$(date +%s.%N)

echo "Running strict generation with detailed logging..."
output=$(dotnet run --project src/tsbindgen/tsbindgen.csproj -- \
    generate -d ~/dotnet/shared/Microsoft.NETCore.App/10.0.0-rc.1.25451.107 \
    -o .tests/perf-baseline --strict --logs PhaseGate,SCC,HonestEmission 2>&1)

# Capture end time
end_time=$(date +%s.%N)

# Calculate total wall time
wall_time=$(echo "$end_time - $start_time" | bc)

# Extract phase timings from logs (if available)
scc_time=$(echo "$output" | grep -o "SCC pass completed in [0-9.]*ms" | grep -o "[0-9.]*" || echo "N/A")
honest_time=$(echo "$output" | grep -o "Honest emission planning completed in [0-9.]*ms" | grep -o "[0-9.]*" || echo "N/A")

# Extract stats
namespaces=$(echo "$output" | grep "Namespaces:" | grep -o "[0-9]*" || echo "N/A")
types=$(echo "$output" | grep "Types:" | grep -o "[0-9]*" || echo "N/A")
members=$(echo "$output" | grep "Members:" | grep -o "[0-9]*" || echo "N/A")

# Write baseline document
cat > docs/strict-mode-perf-baseline.md <<EOF
# Strict Mode Performance Baseline

Captured: $(date -u +"%Y-%m-%d %H:%M:%S UTC")

## Environment

- Machine: $(uname -n)
- OS: $(uname -s) $(uname -r)
- .NET: $(dotnet --version)
- BCL: Microsoft.NETCore.App/10.0.0-rc.1.25451.107

## Baseline Metrics

### Wall Time
- **Total generation time**: ${wall_time}s

### Phase Timings
- **SCC pass**: ${scc_time}ms
- **Honest emission planning**: ${honest_time}ms

### Output Statistics
- **Namespaces**: ${namespaces}
- **Types**: ${types}
- **Members**: ${members}

## Notes

This baseline was captured on the \`feat/strict-hardening-determinism\` branch after:
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
EOF

echo "✓ Baseline captured"
echo ""
echo "Results:"
echo "  Wall time:      ${wall_time}s"
echo "  SCC pass:       ${scc_time}ms"
echo "  Honest emission: ${honest_time}ms"
echo "  Output:         ${namespaces} namespaces, ${types} types, ${members} members"
echo ""
echo "Baseline written to: docs/strict-mode-perf-baseline.md"
echo ""
