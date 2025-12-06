#!/bin/bash
# ======================================================
# Multi-Arity Family: Import Correctness Test
# ======================================================
# Verifies that multi-arity family types can be imported
# and used correctly with various arities.

set -e

echo "================================================"
echo "Multi-Arity Import Correctness Test"
echo "================================================"
echo

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
BCL_DIR="${PROJECT_ROOT}/.tests/bcl"
TEST_DIR="${PROJECT_ROOT}/.tests/multiarity-import"

# Ensure BCL is generated
if [[ ! -d "$BCL_DIR" ]]; then
    echo "[ERROR] BCL directory not found: $BCL_DIR"
    echo "Run: bash test/scripts/capture-baseline.sh"
    exit 1
fi

# Create test directory
rm -rf "$TEST_DIR"
mkdir -p "$TEST_DIR"

echo "[1/3] Creating test file..."

# Create test TypeScript file that uses multi-arity families
cat > "$TEST_DIR/test-multiarity.ts" << 'EOF'
// Test file for multi-arity family import correctness
import type { ValueTuple, Action, Func } from './System.js';
import type { Task } from './System.Threading.Tasks.js';

// ========== ValueTuple Tests ==========
// Arity 0 (no type args - should resolve to non-generic ValueTuple)
type VT0 = ValueTuple;

// Arity 1
type VT1 = ValueTuple<string>;

// Arity 2
type VT2 = ValueTuple<string, number>;

// Arity 3
type VT3 = ValueTuple<string, number, boolean>;

// ========== Action Tests ==========
// Arity 0 (no type args - should resolve to non-generic Action)
type Act0 = Action;

// Arity 1
type Act1 = Action<string>;

// Arity 2
type Act2 = Action<string, number>;

// ========== Func Tests ==========
// Arity 1 (return type only)
type Fn1 = Func<string>;

// Arity 2 (1 arg + return)
type Fn2 = Func<string, number>;

// Arity 3 (2 args + return)
type Fn3 = Func<string, number, boolean>;

// ========== Task Tests ==========
// Arity 0 (non-generic Task)
type T0 = Task;

// Arity 1 (Task<T>)
type T1 = Task<string>;

// ========== Function signatures using families ==========
declare function getKeyPair(): ValueTuple<string, string>;
declare function process(callback: Action<string, number>): void;
declare function transform<T, U>(fn: Func<T, U>): Promise<U>;
declare function asyncOp(): Task<number>;

// ========== Usage test ==========
const pair = getKeyPair();
process((s, n) => console.log(s, n));
const result = transform((x: number) => x.toString());
const task = asyncOp();

export { VT0, VT1, VT2, VT3, Act0, Act1, Act2, Fn1, Fn2, Fn3, T0, T1 };
EOF

echo "[2/3] Copying BCL facades..."

# Copy System.d.ts for import resolution
cp "$BCL_DIR/System.d.ts" "$TEST_DIR/"
cp -r "$BCL_DIR/System" "$TEST_DIR/" 2>/dev/null || true

# Copy System.Threading.Tasks.d.ts for Task import
cp "$BCL_DIR/System.Threading.Tasks.d.ts" "$TEST_DIR/"
cp -r "$BCL_DIR/System.Threading.Tasks" "$TEST_DIR/" 2>/dev/null || true

# Create minimal tsconfig
cat > "$TEST_DIR/tsconfig.json" << 'EOF'
{
  "compilerOptions": {
    "target": "ES2020",
    "module": "ESNext",
    "moduleResolution": "bundler",
    "strict": true,
    "noEmit": true,
    "skipLibCheck": true
  },
  "include": ["test-multiarity.ts"]
}
EOF

echo "[3/3] Running TypeScript compilation..."

cd "$TEST_DIR"

# Run tsc and capture output
if npx tsc --noEmit 2>&1; then
    echo
    echo "================================================"
    echo "[PASSED] Multi-arity import test compiled successfully"
    echo "================================================"
    rm -rf "$TEST_DIR"
    exit 0
else
    echo
    echo "================================================"
    echo "[FAILED] Multi-arity import test has TypeScript errors"
    echo "================================================"
    echo
    echo "Test file location: $TEST_DIR/test-multiarity.ts"
    exit 1
fi
