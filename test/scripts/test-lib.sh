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
if ! dotnet run --project "$PROJECT_ROOT/src/DotnetBindgen/DotnetBindgen.csproj" -- \
    generate -d "$DOTNET_RUNTIME" \
    -o "$LIB_TEST_DIR/bcl-types" \
    > "$LIB_TEST_DIR/bcl-gen.txt" 2>&1; then
    echo -e "${RED}❌ FAILED: BCL generation failed${NC}"
    tail -50 "$LIB_TEST_DIR/bcl-gen.txt"
    exit 1
fi

bcl_namespaces=$(find "$LIB_TEST_DIR/bcl-types" -mindepth 1 -maxdepth 1 -type d | wc -l)
echo "          ✓ BCL generation succeeded ($bcl_namespaces namespaces)"

# Create package.json for BCL library (required by LibraryContractLoader)
# In real usage, the library package would have this file
echo '{"name": "@test/bcl-types", "version": "1.0.0"}' > "$LIB_TEST_DIR/bcl-types/package.json"
echo "          ✓ Created package.json for BCL library"

# Step 2: Build user library fixture
echo "[3/5] Building user library fixture..."
cd "$PROJECT_ROOT/test/fixtures/UserLib"
if ! dotnet build -c Release > /dev/null 2>&1; then
    echo -e "${RED}❌ FAILED: User library build failed${NC}"
    exit 1
fi
cd "$PROJECT_ROOT"

# Find the DLL (could be in bin/ or artifacts/)
userlib_dll=""
if [ -f "$PROJECT_ROOT/test/fixtures/UserLib/bin/Release/net10.0/UserLib.dll" ]; then
    userlib_dll="$PROJECT_ROOT/test/fixtures/UserLib/bin/Release/net10.0/UserLib.dll"
elif [ -f "$PROJECT_ROOT/artifacts/bin/UserLib/Release/net10.0/UserLib.dll" ]; then
    userlib_dll="$PROJECT_ROOT/artifacts/bin/UserLib/Release/net10.0/UserLib.dll"
else
    echo -e "${RED}❌ FAILED: User library DLL not found${NC}"
    exit 1
fi

echo "          ✓ User library fixture built ($userlib_dll)"

# Step 3: Generate user library WITHOUT --lib (should emit everything)
echo "[4/5] Generating user library without --lib (baseline)..."
if ! dotnet run --project "$PROJECT_ROOT/src/DotnetBindgen/DotnetBindgen.csproj" -- \
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
if ! dotnet run --project "$PROJECT_ROOT/src/DotnetBindgen/DotnetBindgen.csproj" -- \
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

# ============================================================
# REGRESSION TESTS FOR EXTERNAL IMPORT RESOLUTION
# These tests detect the failure modes when --lib doesn't work correctly
# ============================================================

echo ""
echo "[6/8] Checking for relative imports to BCL (regression test)..."
# If --lib fails, imports would look like "../../System/internal/index.js" or "../System/"
if grep -rE '"\.\./.*System|"\.\./.*Microsoft' "$LIB_TEST_DIR/user-lib-filtered/" --include="*.d.ts" 2>/dev/null; then
    echo -e "${RED}❌ FAILED: Found relative imports to BCL namespaces${NC}"
    echo "          This indicates library mode import resolution is broken"
    exit 1
fi
echo "          ✓ No relative imports to BCL namespaces"

echo ""
echo "[7/8] Checking for 'unknown' type leakage (regression test)..."
# If type resolution fails, BCL types become 'unknown'
# Check for patterns like ": unknown" that indicate unresolved types
# Note: grep returns exit 1 when no matches, so we use || true to prevent set -e from failing
unknown_count=$(grep -rE ': unknown[^a-zA-Z]|: unknown$' "$LIB_TEST_DIR/user-lib-filtered/" --include="*.d.ts" 2>/dev/null | wc -l || true)
if [ "$unknown_count" -gt 0 ]; then
    echo -e "${RED}❌ FAILED: Found $unknown_count instances of 'unknown' type${NC}"
    echo "          This indicates BCL type resolution failed"
    grep -rE ': unknown[^a-zA-Z]|: unknown$' "$LIB_TEST_DIR/user-lib-filtered/" --include="*.d.ts" 2>/dev/null | head -5 || true
    exit 1
fi
echo "          ✓ No 'unknown' type leakage found"

echo ""
echo "[8/8] Checking for package specifier imports (regression test)..."
# Correct imports should use the library package name, not relative paths
# The BCL library should have a package.json with a name field
bcl_package_name=$(jq -r '.name // empty' "$LIB_TEST_DIR/bcl-types/package.json" 2>/dev/null || echo "")
if [ -n "$bcl_package_name" ]; then
    # Check that imports use the package name (e.g., "@tsonic/dotnet/System.js")
    import_count=$(grep -c "$bcl_package_name/" "$LIB_TEST_DIR/user-lib-filtered/MyCompany.Utils/internal/index.d.ts" 2>/dev/null || echo "0")
    if [ "$import_count" -eq 0 ]; then
        echo -e "${YELLOW}⚠ WARNING: No package specifier imports found for '$bcl_package_name'${NC}"
        echo "          (BCL types may not be referenced from user library)"
    else
        echo "          ✓ Found $import_count package specifier imports for '$bcl_package_name'"
    fi
else
    echo "          (Skipped: BCL package.json has no 'name' field for package specifier test)"
fi

# Count types in user namespace
user_types=$(find "$LIB_TEST_DIR/user-lib-filtered/MyCompany.Utils" -name "*.d.ts" | wc -l)
echo ""
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
echo "  ✓ No relative imports to BCL (regression)"
echo "  ✓ No 'unknown' type leakage (regression)"
echo ""
