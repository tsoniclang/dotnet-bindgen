#!/bin/bash
# Library mode test - validates --lib mode with real user assembly
# Tests that user assemblies only emit their own types, not BCL types

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "================================================"
echo "Library Mode Test"
echo "================================================"
echo ""

# Initialize runtime
init_runtime

# Test output directory
LIB_TEST_DIR="$TESTS_DIR/lib-harness"

# Clean previous runs
echo "[1/5] Cleaning previous test runs..."
rm -rf "$LIB_TEST_DIR"
mkdir -p "$LIB_TEST_DIR"

# Step 1: Generate BCL types (the library contract)
echo "[2/5] Generating BCL types (library contract)..."
if ! dotnet run --project "$PROJECT_ROOT/src/tsbindgen/tsbindgen.csproj" -- \
    generate -d "$DOTNET_RUNTIME" \
    -o "$LIB_TEST_DIR/bcl-types" \
    > "$LIB_TEST_DIR/bcl-gen.txt" 2>&1; then
    echo -e "${RED}❌ FAILED: BCL generation failed${NC}"
    tail -50 "$LIB_TEST_DIR/bcl-gen.txt"
    exit 1
fi

bcl_namespaces=$(find "$LIB_TEST_DIR/bcl-types" -mindepth 1 -maxdepth 1 -type d | wc -l)
echo "          ✓ BCL generation succeeded ($bcl_namespaces namespaces)"

# Step 2: Build user library fixture
echo "[3/5] Building user library fixture..."
cd "$PROJECT_ROOT/scripts/harness/fixtures/UserLib"
if ! dotnet build -c Release > /dev/null 2>&1; then
    echo -e "${RED}❌ FAILED: User library build failed${NC}"
    exit 1
fi
cd "$PROJECT_ROOT"

# Find the DLL (could be in bin/ or artifacts/)
userlib_dll=""
if [ -f "$PROJECT_ROOT/scripts/harness/fixtures/UserLib/bin/Release/net10.0/UserLib.dll" ]; then
    userlib_dll="$PROJECT_ROOT/scripts/harness/fixtures/UserLib/bin/Release/net10.0/UserLib.dll"
elif [ -f "$PROJECT_ROOT/artifacts/bin/UserLib/Release/net10.0/UserLib.dll" ]; then
    userlib_dll="$PROJECT_ROOT/artifacts/bin/UserLib/Release/net10.0/UserLib.dll"
else
    echo -e "${RED}❌ FAILED: User library DLL not found${NC}"
    exit 1
fi

echo "          ✓ User library fixture built ($userlib_dll)"

# Step 3: Generate user library WITHOUT --lib (should emit everything)
echo "[4/5] Generating user library without --lib (baseline)..."
if ! dotnet run --project "$PROJECT_ROOT/src/tsbindgen/tsbindgen.csproj" -- \
    generate -a "$userlib_dll" \
    -d "$DOTNET_RUNTIME" \
    -o "$LIB_TEST_DIR/user-lib-full" \
    > "$LIB_TEST_DIR/user-full.txt" 2>&1; then
    echo -e "${RED}❌ FAILED: User library generation (full) failed${NC}"
    tail -50 "$LIB_TEST_DIR/user-full.txt"
    exit 1
fi

full_namespaces=$(find "$LIB_TEST_DIR/user-lib-full" -mindepth 1 -maxdepth 1 -type d | wc -l)
echo "          ✓ User library (full) generated: $full_namespaces namespaces"
echo "          (Includes both user types AND BCL types)"

# Step 4: Generate user library WITH --lib (should only emit user types)
echo "[5/5] Generating user library with --lib (filtered)..."
if ! dotnet run --project "$PROJECT_ROOT/src/tsbindgen/tsbindgen.csproj" -- \
    generate -a "$userlib_dll" \
    -d "$DOTNET_RUNTIME" \
    -o "$LIB_TEST_DIR/user-lib-filtered" \
    --lib "$LIB_TEST_DIR/bcl-types" \
    > "$LIB_TEST_DIR/user-filtered.txt" 2>&1; then
    echo -e "${RED}❌ FAILED: User library generation (--lib) failed${NC}"
    tail -100 "$LIB_TEST_DIR/user-filtered.txt"
    exit 1
fi

filtered_namespaces=$(find "$LIB_TEST_DIR/user-lib-filtered" -mindepth 1 -maxdepth 1 -type d | wc -l)
echo "          ✓ User library (--lib) generated: $filtered_namespaces namespaces"

# Check for LIB001-003 errors
if grep -q "LIB00[123]" "$LIB_TEST_DIR/user-filtered.txt"; then
    echo -e "${RED}❌ FAILED: Library mode validation errors detected${NC}"
    grep "LIB00[123]" "$LIB_TEST_DIR/user-filtered.txt" | head -20
    exit 1
fi

echo "          ✓ No LIB001-003 validation errors"

# Verify filtering happened
if [ "$filtered_namespaces" -ge "$full_namespaces" ]; then
    echo -e "${RED}❌ FAILED: --lib didn't filter anything ($filtered_namespaces >= $full_namespaces)${NC}"
    exit 1
fi

echo "          ✓ Filtering worked: $full_namespaces → $filtered_namespaces namespaces"

# Verify user namespace is present
if [ ! -d "$LIB_TEST_DIR/user-lib-filtered/MyCompany.Utils" ]; then
    echo -e "${RED}❌ FAILED: MyCompany.Utils namespace missing from filtered output${NC}"
    exit 1
fi

echo "          ✓ User namespace (MyCompany.Utils) present in output"

# Verify BCL namespaces are NOT present (they're in the library contract)
if [ -d "$LIB_TEST_DIR/user-lib-filtered/System" ]; then
    echo -e "${RED}❌ FAILED: System namespace should NOT be in filtered output (it's in --lib)${NC}"
    exit 1
fi

echo "          ✓ BCL namespaces correctly excluded from output"

# Count types in user namespace
user_types=$(find "$LIB_TEST_DIR/user-lib-filtered/MyCompany.Utils" -name "*.d.ts" | wc -l)
echo "          User types emitted: $user_types"

echo ""
echo "================================================"
echo -e "${GREEN}✓ LIBRARY MODE FULLY VERIFIED${NC}"
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
echo ""
