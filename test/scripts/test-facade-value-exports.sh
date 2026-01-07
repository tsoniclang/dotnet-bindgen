#!/bin/bash
# Test that facade value exports work correctly
# This validates that classes, enums, and structs are exported as values (not type-only)
# so that static methods, enum values, and constructors are accessible.
#
# NOTE: These tests use PascalCase member names because the default mode
# generates PascalCase. The @tsonic/dotnet npm package uses camelCase,
# but this test uses default mode.

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "================================================================"
echo "Testing Facade Value Exports"
echo "================================================================"

# Use cached BCL output
BCL_DIR=$(ensure_bcl default)

# Test directory
TEST_DIR="$TESTS_DIR/facade-value-exports"

# Clean and create test directory
rm -rf "$TEST_DIR"
mkdir -p "$TEST_DIR"

# Create package.json for the test project
cat > "$TEST_DIR/package.json" << 'EOF'
{
  "name": "facade-value-exports-test",
  "type": "module",
  "private": true
}
EOF

# Create tsconfig.json pointing to BCL cache
cat > "$TEST_DIR/tsconfig.json" << EOF
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "ESNext",
    "moduleResolution": "bundler",
    "strict": true,
    "noEmit": true,
    "skipLibCheck": true,
    "paths": {
      "@tsonic/dotnet/*": ["$BCL_DIR/*"]
    }
  },
  "include": ["*.ts"]
}
EOF

# Test 1: Static method access (Console.WriteLine)
echo "Creating test: static-method-access.ts"
cat > "$TEST_DIR/static-method-access.ts" << 'EOF'
// Test: Static method access on a static class
// This verifies that Console is exported as a VALUE, not just a type.
// If exported as type-only, this would fail with "Console only refers to a type"

import { Console } from "@tsonic/dotnet/System.js";

// Access static method - requires value export
Console.WriteLine("Hello from static method test");
Console.Write("Writing without newline");

// Also test other static classes
import { GC, Environment } from "@tsonic/dotnet/System.js";

// GC.Collect() with no args works
GC.Collect();

// Static property access
const osVersion = Environment.OSVersion;

// NOTE: We skip Math.Abs(-5) etc. because those require branded int types.
// The goal of this test is to verify VALUE EXPORTS work (static access),
// not to test branded type compatibility.
EOF

# Test 2: Enum value access (ConsoleColor.Red)
echo "Creating test: enum-value-access.ts"
cat > "$TEST_DIR/enum-value-access.ts" << 'EOF'
// Test: Enum member access
// This verifies that enums are exported as VALUES, not just types.
// If exported as type-only, this would fail with "ConsoleColor only refers to a type"

import { ConsoleColor, ConsoleKey, DayOfWeek } from "@tsonic/dotnet/System.js";

// Access enum members - requires value export
const color = ConsoleColor.Red;
const key = ConsoleKey.Enter;
const day = DayOfWeek.Monday;

// Use in switch
function getColorName(c: ConsoleColor): string {
    switch (c) {
        case ConsoleColor.Red: return "Red";
        case ConsoleColor.Green: return "Green";
        case ConsoleColor.Blue: return "Blue";
        default: return "Unknown";
    }
}

import { FileMode, FileAccess } from "@tsonic/dotnet/System.IO.js";

const mode = FileMode.Create;
const access = FileAccess.ReadWrite;
EOF

# Test 3: Generic class construction (new List<string>())
echo "Creating test: generic-class-construction.ts"
cat > "$TEST_DIR/generic-class-construction.ts" << 'EOF'
// Test: Generic class construction and static access
// This verifies that generic classes are exported as VALUES.
// We use the friendly aliases (List, Dictionary) - arity names (List_1, Dictionary_2)
// are internal implementation details not exported from facade.

import { List, Dictionary } from "@tsonic/dotnet/System.Collections.Generic.js";

// Construction requires value export
const stringList = new List<string>();
const numberList = new List<number>();
const dict = new Dictionary<string, number>();

// Instance method access
stringList.Add("hello");
stringList.Add("world");
const count = stringList.Count;

// Also test Exception (a common non-generic class)
import { Exception } from "@tsonic/dotnet/System.js";
const err = new Exception();
const msg = err.Message;
EOF

# Test 4: Interface remains type-only (regression test)
echo "Creating test: interface-type-only.ts"
cat > "$TEST_DIR/interface-type-only.ts" << 'EOF'
// Test: Interfaces should remain type-only exports
// This is a regression test to ensure we don't accidentally value-export interfaces.
// Interfaces have no runtime value, so type-only is correct.
// We use friendly names (IEnumerable, IList) not arity names (IEnumerable_1, IList_1).

import type { IEnumerable, IList, IDictionary } from "@tsonic/dotnet/System.Collections.Generic.js";
import type { IDisposable, IComparable } from "@tsonic/dotnet/System.js";

// Use as type annotations - this should always work
function process(items: IEnumerable<string>): void {
    // Implementation
}

function acceptList(list: IList<number>): void {
    // Implementation
}

let disposable: IDisposable;
let comparable: IComparable<string>;  // IComparable requires type arg

// Note: We can't do "new IEnumerable()" because interfaces have no constructor.
// This test just verifies the types are usable as type annotations.
EOF

# Test 5: Struct value export (DateTime, Point, etc.)
echo "Creating test: struct-value-export.ts"
cat > "$TEST_DIR/struct-value-export.ts" << 'EOF'
// Test: Struct value export
// Structs in our model are emitted as classes, so they need value exports
// for construction and static member access.

import { DateTime, TimeSpan, Guid } from "@tsonic/dotnet/System.js";

// Static property access - requires value export
const now = DateTime.Now;
const utcNow = DateTime.UtcNow;
const minValue = DateTime.MinValue;

// Static method access (with string args to avoid branded type issues)
const parsed = DateTime.Parse("2025-01-01");
const newGuid = Guid.NewGuid();

// TimeSpan static property access
const zero = TimeSpan.Zero;

// NOTE: We skip TimeSpan.FromHours(1) because it requires branded double type.
// The goal is to verify VALUE EXPORTS work (static access is visible),
// not to test branded type compatibility.
EOF

# Test 6: Abstract class non-instantiability (Stream cannot be new'd)
echo "Creating test: abstract-class-not-instantiable.ts"
cat > "$TEST_DIR/abstract-class-not-instantiable.ts" << 'EOF'
// Test: Abstract classes should NOT be instantiable
// This is a negative test - we expect TypeScript to reject "new Stream()"
// because Stream is abstract and has no new() signature in its const export.

import { Stream } from "@tsonic/dotnet/System.IO.js";

// This should fail to compile because Stream is abstract
// @ts-expect-error - Stream is abstract, cannot be instantiated
const stream = new Stream();

// But static access should work
const nullStream = Stream.Null;
EOF

echo ""
echo "Running TypeScript compiler (tsc --noEmit)..."
echo ""

cd "$TEST_DIR"

# Run tsc
if run_tsc --noEmit 2>&1; then
    echo ""
    echo "================================================================"
    echo -e "${GREEN}✓ ALL FACADE VALUE EXPORT TESTS PASSED${NC}"
    echo "================================================================"
    echo ""
    echo "Verified:"
    echo "  - Static method access (Console.WriteLine)"
    echo "  - Enum value access (ConsoleColor.Red)"
    echo "  - Generic class construction (new List<T>())"
    echo "  - Interface type-only usage (IEnumerable<T>)"
    echo "  - Struct value export (DateTime.Now)"
    echo "  - Abstract class non-instantiability (new Stream() fails)"
    exit 0
else
    echo ""
    echo "================================================================"
    echo -e "${RED}✗ FACADE VALUE EXPORT TESTS FAILED${NC}"
    echo "================================================================"
    echo ""
    echo "TypeScript compilation failed. This indicates that either:"
    echo "  1. A class/enum/struct is incorrectly exported as type-only"
    echo "  2. An internal value name doesn't exist in the internal module"
    echo "  3. The facade export path is incorrect"
    echo "  4. @ts-expect-error didn't catch an expected error"
    echo ""
    exit 1
fi
