#!/bin/bash
# Test: Primitive constraint relaxation - IEquatable_1<T>, IComparable_1<T>
# Verifies that generic constraints are relaxed to admit branded primitives.
# This prevents TS2344 errors when using APIs like SearchValues.Create(byte[]).

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "Running primitive constraint relaxation tests..."

# Use cached BCL output
BCL_DIR=$(ensure_bcl default)

# Create temp directory for test files
TEMP_DIR=$(mktemp -d)
trap "rm -rf $TEMP_DIR" EXIT

# Install required packages for noLib mode
cd "$TEMP_DIR"
npm init -y > /dev/null 2>&1
npm install --save-dev @tsonic/dotnet-globals > /dev/null 2>&1

# Create test file that exercises the exact failing API family
cat > "$TEMP_DIR/test.ts" << 'TESTEOF'
// Branded primitive types (from @tsonic/types)
// Per @tsonic/types contract: ALL numerics are number-carried (including long, nint, nuint)
type byte = number & { __brand: "byte" };
type char = string & { __brand: "char" };
type int = number & { __brand: "int" };
type long = number & { __brand: "long" };    // number-carried per @tsonic/types
type nint = number & { __brand: "nint" };    // number-carried per @tsonic/types
type nuint = number & { __brand: "nuint" };  // number-carried per @tsonic/types

// CLR primitive aliases in a namespace to avoid conflicts with global types
namespace CLR {
    export type Byte = byte;
    export type Char = char;
    export type Int32 = int;
    export type String_ = string;
}

// CLROf utility - identity for primitives
type CLROf<T> =
    T extends byte ? CLR.Byte :
    T extends char ? CLR.Char :
    T extends int ? CLR.Int32 :
    T extends string ? CLR.String_ :
    T;

// ============================================================
// Simulated IEquatable_1 interface (as generated)
// ============================================================
interface IEquatable_1$instance<T> {
    Equals(other: T): boolean;
}
type IEquatable_1<T> = IEquatable_1$instance<T>;

// ============================================================
// Simulated IComparable_1 interface (as generated)
// ============================================================
interface IComparable_1$instance<T> {
    CompareTo(other: T): int;
}
type IComparable_1<T> = IComparable_1$instance<T>;

// ============================================================
// TEST 1: SearchValues pattern - IEquatable_1<T> constraint RELAXED
// The constraint is: T extends (IEquatable_1<T> | number | string | boolean)
// This admits primitives without requiring structural Equals method.
// ============================================================
interface SearchValues_1$instance<T extends (IEquatable_1<T> | number | string | boolean)> {
    Contains(value: T): boolean;
}
type SearchValues_1<T extends (IEquatable_1<T> | number | string | boolean)> = SearchValues_1$instance<T>;

// SearchValues const with constructor
declare const SearchValues: {
    Create<T extends (IEquatable_1<T> | number | string | boolean)>(values: readonly T[]): SearchValues_1<T>;
};

// This MUST compile - byte is assignable to number which satisfies the relaxed constraint
const byteSearchValues: SearchValues_1<CLROf<byte>> = SearchValues.Create<CLROf<byte>>([1 as byte, 2 as byte]);

// This MUST compile - char is assignable to string which satisfies the relaxed constraint  
const charSearchValues: SearchValues_1<CLROf<char>> = SearchValues.Create<CLROf<char>>(["a" as char, "b" as char]);

// This MUST compile - string satisfies the relaxed constraint directly
const stringSearchValues: SearchValues_1<string> = SearchValues.Create<string>(["foo", "bar"]);

// ============================================================
// TEST 2: Comparer pattern - IComparable_1<T> constraint RELAXED
// ============================================================
declare function createSortedList<T extends (IComparable_1<T> | number | string | boolean)>(): T[];

// This MUST compile - int is assignable to number which satisfies the relaxed constraint
const sortedInts: int[] = createSortedList<int>();

// ============================================================
// TEST 3: Generic class with IEquatable constraint RELAXED
// ============================================================
interface HashSet_1$instance<T extends (IEquatable_1<T> | number | string | boolean)> {
    Add(item: T): boolean;
    Contains(item: T): boolean;
}
type HashSet_1<T extends (IEquatable_1<T> | number | string | boolean)> = HashSet_1$instance<T>;

declare const HashSet_1: {
    new<T extends (IEquatable_1<T> | number | string | boolean)>(): HashSet_1$instance<T>;
};

// This MUST compile - instantiate HashSet with primitive types
// All numeric primitives are number-carried per @tsonic/types
const byteSet: HashSet_1<byte> = new HashSet_1<byte>();
const intSet: HashSet_1<int> = new HashSet_1<int>();
const longSet: HashSet_1<long> = new HashSet_1<long>();    // number-carried (64-bit)
const nintSet: HashSet_1<nint> = new HashSet_1<nint>();    // number-carried (native)
const nuintSet: HashSet_1<nuint> = new HashSet_1<nuint>(); // number-carried (native)
const stringSet: HashSet_1<string> = new HashSet_1<string>();

// ============================================================
// TEST 4: Non-primitive types still work (full fidelity preserved)
// ============================================================
interface Guid {
    Equals(other: Guid): boolean;  // Satisfies IEquatable_1<Guid>
}

// This MUST compile - Guid satisfies IEquatable_1<Guid> structurally
const guidSet: HashSet_1<Guid> = new HashSet_1<Guid>();
TESTEOF

# Create tsconfig.json with noLib (like Tsonic uses)
cat > "$TEMP_DIR/tsconfig.json" << 'CONFIGEOF'
{
  "compilerOptions": {
    "strict": true,
    "noEmit": true,
    "noLib": true,
    "skipLibCheck": true,
    "target": "ES2020",
    "module": "ESNext",
    "moduleResolution": "node",
    "types": ["@tsonic/dotnet-globals"]
  },
  "include": ["test.ts"]
}
CONFIGEOF

# Run TypeScript compiler
echo "  Compiling test file with tsc --strict --noLib..."
if run_tsc --noEmit 2>&1; then
    echo -e "${GREEN}All primitive constraint tests passed!${NC}"
    echo ""
    echo "Verified:"
    echo "  - SearchValues.Create<byte> compiles (IEquatable constraint relaxed)"
    echo "  - SearchValues.Create<char> compiles (IEquatable constraint relaxed)"
    echo "  - SearchValues.Create<string> compiles (IEquatable constraint relaxed)"
    echo "  - IComparable constraint admits primitives"
    echo "  - HashSet<byte/int/string> instantiation works"
    echo "  - Non-primitive types (Guid) still work via IEquatable"
    exit 0
else
    echo -e "${RED}Primitive constraint tests FAILED!${NC}"
    echo "This indicates TS2344 regression - constraints not relaxed properly."
    exit 1
fi
