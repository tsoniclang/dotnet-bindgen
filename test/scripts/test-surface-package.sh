#!/bin/bash

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "================================================"
echo "Surface Package Emitter"
echo "================================================"
echo ""

init_runtime

SURFACE_ONLY_OUT="$TESTS_DIR/surface-package-only"
MODULE_ONLY_OUT="$TESTS_DIR/surface-package-module-only"
WITH_ASSEMBLY_OUT="$TESTS_DIR/surface-package-with-assembly"

rm -rf "$SURFACE_ONLY_OUT" "$MODULE_ONLY_OUT" "$WITH_ASSEMBLY_OUT"
mkdir -p "$SURFACE_ONLY_OUT" "$MODULE_ONLY_OUT" "$WITH_ASSEMBLY_OUT"

echo "[1/4] Building DotnetBindgen..."
dotnet build "$PROJECT_ROOT/src/DotnetBindgen/DotnetBindgen.csproj" -c Release --verbosity quiet >/dev/null
echo -e "${GREEN}✓ dotnet-bindgen built${NC}"

echo "[2/4] Surface-only generation..."
dotnet run --project "$PROJECT_ROOT/src/DotnetBindgen/DotnetBindgen.csproj" --no-build -c Release -- \
  generate \
  --out-dir "$SURFACE_ONLY_OUT" \
  --surface-package "$PROJECT_ROOT/test/fixtures/SurfacePackage/surface-only.json" \
  >/dev/null

assert_grep '/// <reference path="./globals.d.ts" />' "$SURFACE_ONLY_OUT/index.d.ts" "surface-only prepends index reference"
assert_grep 'interface String {' "$SURFACE_ONLY_OUT/globals.d.ts" "surface-only emits ambient declaration file"
assert_grep '"String"' "$SURFACE_ONLY_OUT/bindings.json" "surface-only emits root bindings"
assert_grep '"typeSemantics"' "$SURFACE_ONLY_OUT/bindings.json" "surface-only emits explicit type semantics"
assert_grep '"contributesTypeIdentity": true' "$SURFACE_ONLY_OUT/bindings.json" "surface-only preserves type-like globals explicitly"
assert_grep '"surfaceMode": "@acme/js"' "$SURFACE_ONLY_OUT/tsonic.bindings.json" "surface-only emits bindings manifest"
assert_grep '"id": "@acme/js"' "$SURFACE_ONLY_OUT/tsonic.surface.json" "surface-only emits surface manifest"

echo "[3/5] Module-only surface generation..."
dotnet run --project "$PROJECT_ROOT/src/DotnetBindgen/DotnetBindgen.csproj" --no-build -c Release -- \
  generate \
  --out-dir "$MODULE_ONLY_OUT" \
  --surface-package "$PROJECT_ROOT/test/fixtures/SurfacePackage/module-only.json" \
  >/dev/null

assert_not_grep 'declare global \{' "$MODULE_ONLY_OUT/node-aliases.d.ts" "module-only surface skips empty declare global block"
if [ "$(sed -n '1p' "$MODULE_ONLY_OUT/node-aliases.d.ts")" = 'declare module "fs" {' ]; then
  test_result PASS "module-only surface starts directly with module declaration"
else
  test_result FAIL "module-only surface starts directly with module declaration"
  echo "    Expected first line: declare module \"fs\" {" >&2
  echo "    Actual first line:   $(sed -n '1p' "$MODULE_ONLY_OUT/node-aliases.d.ts")" >&2
  exit 1
fi
assert_grep 'Example.Runtime.Map`2' "$MODULE_ONLY_OUT/bindings.json" "surface bindings keep generic CLR names readable"
assert_not_grep '"staticType": null' "$MODULE_ONLY_OUT/bindings.json" "surface bindings omit null staticType"

echo "[4/5] Building UserLib fixture..."
dotnet build "$PROJECT_ROOT/test/fixtures/UserLib/UserLib.csproj" -c Release --verbosity quiet >/dev/null

USERLIB_DLL="$(dotnet msbuild "$PROJECT_ROOT/test/fixtures/UserLib/UserLib.csproj" -nologo -p:Configuration=Release -getProperty:TargetPath | tail -n 1 | tr -d '\r')"

if [ ! -f "$USERLIB_DLL" ]; then
  echo -e "${RED}❌ FAILED: missing UserLib.dll${NC}"
  exit 1
fi

echo "[5/5] Assembly + surface package generation..."
dotnet run --project "$PROJECT_ROOT/src/DotnetBindgen/DotnetBindgen.csproj" --no-build -c Release -- \
  generate \
  -a "$USERLIB_DLL" \
  -d "$DOTNET_RUNTIME" \
  --out-dir "$WITH_ASSEMBLY_OUT" \
  --surface-package "$PROJECT_ROOT/test/fixtures/SurfacePackage/with-assembly.json" \
  >/dev/null

assert_grep '/// <reference path="./ambient.d.ts" />' "$WITH_ASSEMBLY_OUT/index.d.ts" "assembly mode prepends surface reference"
assert_grep 'interface Number {' "$WITH_ASSEMBLY_OUT/ambient.d.ts" "assembly mode emits surface declarations"
assert_grep 'export { Calculator as Calculator }' "$WITH_ASSEMBLY_OUT/MyCompany.Utils.d.ts" "assembly mode still emits reflected declarations"
assert_grep '"emitSemantics"' "$WITH_ASSEMBLY_OUT/MyCompany.Utils/bindings.json" "assembly mode emits embedded member semantics"
assert_grep '"callStyle": "static"' "$WITH_ASSEMBLY_OUT/MyCompany.Utils/bindings.json" "embedded member semantics preserve declared static style"

echo "[6/6] Standalone bindings-semantics overlay..."
dotnet run --project "$PROJECT_ROOT/src/DotnetBindgen/DotnetBindgen.csproj" --no-build -c Release -- \
  generate \
  -a "$USERLIB_DLL" \
  -d "$DOTNET_RUNTIME" \
  --out-dir "$WITH_ASSEMBLY_OUT" \
  --surface-package "$PROJECT_ROOT/test/fixtures/SurfacePackage/with-assembly.json" \
  --bindings-semantics "$PROJECT_ROOT/test/fixtures/SurfacePackage/with-assembly-semantics.json" \
  >/dev/null

assert_grep '"callStyle": "receiver"' "$WITH_ASSEMBLY_OUT/MyCompany.Utils/bindings.json" "standalone bindings-semantics overlays emit receiver style metadata"

echo ""
echo "================================================"
echo -e "${GREEN}✓ TEST PASSED${NC}"
echo "================================================"
