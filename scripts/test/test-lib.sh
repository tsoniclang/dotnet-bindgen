#!/bin/bash
# Library mode test - validates --lib mode with real user assembly
# Tests that user assemblies only emit their own types, not BCL types

set -euo pipefail

echo "================================================"
echo "Library Mode Test"
echo "================================================"
echo ""

# Clean previous runs
echo "[1/5] Cleaning previous test runs..."
rm -rf .tests/lib-harness
mkdir -p .tests/lib-harness

# Step 1: Generate BCL types (the library contract)
echo "[2/5] Generating BCL types (library contract)..."
dotnet run --project src/tsbindgen/tsbindgen.csproj -- \
    generate -d ~/dotnet/shared/Microsoft.NETCore.App/10.0.0-rc.1.25451.107 \
    -o .tests/lib-harness/bcl-types \
    --strict > .tests/lib-harness/bcl-gen.txt 2>&1

if [ $? -ne 0 ]; then
    echo "❌ FAILED: BCL generation failed"
    tail -50 .tests/lib-harness/bcl-gen.txt
    exit 1
fi

bcl_namespaces=$(find .tests/lib-harness/bcl-types -mindepth 1 -maxdepth 1 -type d | wc -l)
echo "          ✓ BCL generation succeeded ($bcl_namespaces namespaces)"

# Step 2: Build user library fixture
echo "[3/5] Building user library fixture..."
cd scripts/harness/fixtures/UserLib
dotnet build -c Release > /dev/null 2>&1
cd ../../../..

# Find the DLL (could be in bin/ or artifacts/)
userlib_dll=""
if [ -f scripts/harness/fixtures/UserLib/bin/Release/net10.0/UserLib.dll ]; then
    userlib_dll="scripts/harness/fixtures/UserLib/bin/Release/net10.0/UserLib.dll"
elif [ -f ./artifacts/bin/UserLib/Release/net10.0/UserLib.dll ]; then
    userlib_dll="./artifacts/bin/UserLib/Release/net10.0/UserLib.dll"
else
    echo "❌ FAILED: User library DLL not found"
    exit 1
fi

echo "          ✓ User library fixture built ($userlib_dll)"

# Step 3: Generate user library WITHOUT --lib (should emit everything)
echo "[4/5] Generating user library without --lib (baseline)..."
dotnet run --project src/tsbindgen/tsbindgen.csproj -- \
    generate -a $userlib_dll \
    -d ~/dotnet/shared/Microsoft.NETCore.App/10.0.0-rc.1.25451.107 \
    -o .tests/lib-harness/user-lib-full \
    --strict > .tests/lib-harness/user-full.txt 2>&1

if [ $? -ne 0 ]; then
    echo "❌ FAILED: User library generation (full) failed"
    tail -50 .tests/lib-harness/user-full.txt
    exit 1
fi

full_namespaces=$(find .tests/lib-harness/user-lib-full -mindepth 1 -maxdepth 1 -type d | wc -l)
echo "          ✓ User library (full) generated: $full_namespaces namespaces"
echo "          (Includes both user types AND BCL types)"

# Step 4: Generate user library WITH --lib (should only emit user types)
echo "[5/5] Generating user library with --lib (filtered)..."
dotnet run --project src/tsbindgen/tsbindgen.csproj -- \
    generate -a $userlib_dll \
    -d ~/dotnet/shared/Microsoft.NETCore.App/10.0.0-rc.1.25451.107 \
    -o .tests/lib-harness/user-lib-filtered \
    --lib .tests/lib-harness/bcl-types \
    --strict > .tests/lib-harness/user-filtered.txt 2>&1

if [ $? -ne 0 ]; then
    echo "❌ FAILED: User library generation (--lib) failed"
    tail -100 .tests/lib-harness/user-filtered.txt
    exit 1
fi

filtered_namespaces=$(find .tests/lib-harness/user-lib-filtered -mindepth 1 -maxdepth 1 -type d | wc -l)
echo "          ✓ User library (--lib) generated: $filtered_namespaces namespaces"

# Check for LIB001-003 errors
if grep -q "LIB00[123]" .tests/lib-harness/user-filtered.txt; then
    echo "❌ FAILED: Library mode validation errors detected"
    grep "LIB00[123]" .tests/lib-harness/user-filtered.txt | head -20
    exit 1
fi

echo "          ✓ No LIB001-003 validation errors"

# Verify filtering happened
if [ "$filtered_namespaces" -ge "$full_namespaces" ]; then
    echo "❌ FAILED: --lib didn't filter anything ($filtered_namespaces >= $full_namespaces)"
    exit 1
fi

echo "          ✓ Filtering worked: $full_namespaces → $filtered_namespaces namespaces"

# Verify user namespace is present
if [ ! -d .tests/lib-harness/user-lib-filtered/MyCompany.Utils ]; then
    echo "❌ FAILED: MyCompany.Utils namespace missing from filtered output"
    exit 1
fi

echo "          ✓ User namespace (MyCompany.Utils) present in output"

# Verify BCL namespaces are NOT present (they're in the library contract)
if [ -d .tests/lib-harness/user-lib-filtered/System ]; then
    echo "❌ FAILED: System namespace should NOT be in filtered output (it's in --lib)"
    exit 1
fi

echo "          ✓ BCL namespaces correctly excluded from output"

# Count types in user namespace
user_types=$(find .tests/lib-harness/user-lib-filtered/MyCompany.Utils -name "*.d.ts" | wc -l)
echo "          User types emitted: $user_types"

echo ""
echo "================================================"
echo "✓ LIBRARY MODE FULLY VERIFIED"
echo "================================================"
echo ""
echo "Summary:"
echo "  ✓ BCL generation succeeded ($bcl_namespaces namespaces)"
echo "  ✓ User library build succeeded"
echo "  ✓ Full generation: $full_namespaces namespaces (user + BCL)"
echo "  ✓ Filtered generation: $filtered_namespaces namespaces (user only)"
echo "  ✓ BCL types correctly excluded via --lib"
echo "  ✓ User types (MyCompany.Utils) correctly included"
echo "  ✓ No LIB001-003 validation errors"
echo "  ✓ Strict mode passes"
echo ""
