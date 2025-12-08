# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with the tsbindgen project.

## Critical Guidelines

### NEVER ACT WITHOUT EXPLICIT USER APPROVAL

**YOU MUST ALWAYS ASK FOR PERMISSION BEFORE:**

- Making architectural decisions or changes
- Implementing new features or functionality
- Modifying type mapping rules or generation logic
- Changing metadata structure or output format
- Adding new dependencies or packages
- Modifying reflection or MetadataLoadContext usage patterns

**ONLY make changes AFTER the user explicitly approves.** When you identify issues or potential improvements, explain them clearly and wait for the user's decision. Do NOT assume what the user wants or make "helpful" changes without permission.

### ANSWER QUESTIONS AND STOP

**CRITICAL RULE**: If the user asks you a question - whether as part of a larger text or just the question itself - you MUST:

1. **Answer ONLY that question**
2. **STOP your response completely**
3. **DO NOT continue with any other tasks or implementation**
4. **DO NOT proceed with previous tasks**
5. **Wait for the user's next instruction**

This applies to ANY question, even if it seems like part of a larger task or discussion.

### FUNCTIONAL PROGRAMMING STYLE - MANDATORY

**MANDATORY**: This codebase follows strict functional programming principles:

#### All Implementation Code Must Be:

1. **Static classes only** - No instance classes for logic
2. **Pure functions** - No mutable state, no side effects (except I/O)
3. **Immutable data** - Records and data classes are immutable

#### File Naming Convention - NO "-ER" SUFFIX

**NEVER use "-er" or "-or" suffix in file names. This implies agent/doer patterns (OOP thinking).**

```
✅ CORRECT naming (functional style):
- TypeScriptEmit.cs      (noun phrase - "the TypeScript emission")
- MetadataEmit.cs        (noun phrase - "the metadata emission")
- Reflect.cs             (verb as noun - "reflection operations")
- NameTransformation.cs  (noun phrase - "name transformation")

❌ WRONG naming (OOP agent/doer pattern):
- TypeScriptEmitter.cs   (agent that emits)
- MetadataGenerator.cs   (agent that generates)
- TypeMapper.cs          (agent that maps)
- AssemblyProcessor.cs   (agent that processes)
```

#### Code Structure Examples

```csharp
// ✅ CORRECT - Static class with pure functions
public static class TypeScriptEmit
{
    public static string Emit(NamespaceModel model)
    {
        // Pure function - takes input, returns output, no state
        var builder = new StringBuilder();
        // ... build output ...
        return builder.ToString();
    }
}

// ❌ WRONG - Instance class with state
public class TypeScriptEmitter
{
    private readonly Config _config;  // State - not allowed!

    public string Emit(NamespaceModel model)
    {
        // Instance method - not allowed!
    }
}
```

**Immutable Data Classes (Records/Models)**:

```csharp
// ✅ CORRECT - Immutable record
public sealed record TypeModel(
    string ClrName,
    string TsEmitName,
    IReadOnlyList<MethodModel> Methods);

// ❌ WRONG - Mutable class
public class TypeModel
{
    public string ClrName { get; set; }  // Mutable - not allowed!
}
```

**See CODING-STANDARDS.md for complete functional programming guidelines.**

### TYPE EMISSION INVARIANTS

**Non-negotiable rules for type emission:**

1. **tsbindgen never infers CLR identity from a TS type** - No "if `T extends number` then CLR.Int32". All type decisions come from CLR metadata.
2. **tsbindgen only emits types derived from CLR metadata** - TypeScript is for shape/callability/ergonomics, not CLR identity.
3. **Primitive identity does not exist in TS for numerics** - Any API relying on TS to distinguish `Int32` vs `SByte` is invalid. The Tsonic compiler is the numeric authority.

**Primitive emission pattern:**
- Value positions (parameters, returns): lowercase TS aliases (`int`, `char`, `decimal`)
- Generic type arguments: CLR type names (`Int32`, `Char`, `Decimal`)
- Example: `List<int>` → `List_1<Int32>` (not `List_1<int>`)

### NEVER USE AUTOMATED SCRIPTS FOR FIXES

**🚨 CRITICAL RULE: NEVER EVER attempt automated fixes via scripts or mass updates. 🚨**

- **NEVER** create scripts to automate replacements (PowerShell, bash, Python, etc.)
- **NEVER** use sed, awk, grep, or other text processing tools for bulk changes
- **NEVER** write code that modifies multiple files automatically
- **ALWAYS** make changes manually using the Edit tool
- **Even if there are hundreds of similar changes, do them ONE BY ONE**

Automated scripts break syntax in unpredictable ways and destroy codebases.

### GIT SAFETY RULES

#### NEVER DISCARD UNCOMMITTED WORK

**🚨 CRITICAL RULE: NEVER use commands that permanently delete uncommitted changes. 🚨**

These commands cause **PERMANENT DATA LOSS** that cannot be recovered:

- **NEVER** use `git reset --hard`
- **NEVER** use `git reset --soft`
- **NEVER** use `git reset --mixed`
- **NEVER** use `git reset HEAD`
- **NEVER** use `git checkout -- .`
- **NEVER** use `git checkout -- <file>`
- **NEVER** use `git restore` to discard changes
- **NEVER** use `git clean -fd`

**Why this matters for AI sessions:**
- Uncommitted work is invisible to future AI sessions
- Once discarded, changes cannot be recovered
- AI cannot help fix problems it cannot see

**What to do instead:**

| Situation | ❌ WRONG | ✅ CORRECT |
|-----------|---------|-----------|
| Need to switch branches | `git checkout main` (loses changes) | Commit first, then switch |
| Made mistakes | `git reset --hard` | Commit to temp branch, start fresh |
| Want clean slate | `git restore .` | Commit current state, then revert |
| On wrong branch | `git checkout --` | Commit here, then cherry-pick |

**Safe workflow:**

```bash
# Always commit before switching context
git add -A
git commit -m "wip: current progress on feature X"
git checkout other-branch

# If commit was wrong, fix with new commit or revert
git revert HEAD  # Creates new commit that undoes last commit
# OR
git commit -m "fix: correct the previous commit"
```

#### NEVER USE GIT STASH

**🚨 CRITICAL RULE: NEVER use git stash - it hides work and causes data loss. 🚨**

- **NEVER** use `git stash`
- **NEVER** use `git stash push`
- **NEVER** use `git stash pop`
- **NEVER** use `git stash apply`
- **NEVER** use `git stash drop`

**Why stash is dangerous:**
- Stashed changes are invisible to AI sessions
- Easy to forget what's stashed
- Stash can be accidentally dropped
- Causes merge conflicts when applied
- No clear history of when/why stashed

**What to do instead - Use WIP branches:**

```bash
# Instead of stash, create a timestamped WIP branch
git checkout -b wip/feature-name-$(date +%Y%m%d-%H%M%S)
git add -A
git commit -m "wip: in-progress work on feature X"
git push -u origin wip/feature-name-$(date +%Y%m%d-%H%M%S)

# Now switch to other work safely
git checkout main
# ... do other work ...

# Return to your WIP later
git checkout wip/feature-name-20251108-084530
# Continue working...

# When done, squash WIP commits or rebase
git rebase -i main
```

**Benefits of WIP branches over stash:**
- ✅ Work is visible in git history
- ✅ Work is backed up on remote
- ✅ AI can see the work in future sessions
- ✅ Can have multiple WIP branches
- ✅ Clear timestamps show when work was done
- ✅ Can share WIP with others if needed

#### Safe Branch Switching

**ALWAYS commit before switching branches:**

```bash
# Check current status
git status

# If there are changes, commit them first
git add -A
git commit -m "wip: current state before switching"

# NOW safe to switch
git checkout other-branch
```

**If you accidentally started work on wrong branch:**

```bash
# DON'T use git reset or git checkout --
# Instead, commit the work here
git add -A
git commit -m "wip: work started on wrong branch"

# Create correct branch from current state
git checkout -b correct-branch-name

# Previous branch will still have the commit
# You can cherry-pick it or just continue on new branch
```

#### Recovery from Mistakes

If you realize you made a mistake AFTER committing:

```bash
# ✅ CORRECT: Create a fix commit
git commit -m "fix: correct the mistake from previous commit"

# ✅ CORRECT: Revert the bad commit
git revert HEAD

# ❌ WRONG: Try to undo with reset
git reset --hard HEAD~1  # NEVER DO THIS - loses history
```

**If you accidentally committed to main:**

```bash
# DON'T panic or use git reset
# Just create a feature branch from current position
git checkout -b feat/your-feature-name

# Push the branch
git push -u origin feat/your-feature-name

# When merged, it will fast-forward (no conflicts)
# Main will catch up to the same commit
```

### WORKING DIRECTORIES

**IMPORTANT**: Never create temporary files in the project root or src/ directories. Use dedicated gitignored directories for different purposes.

#### .tests/ Directory (Test Output Capture)

**Purpose:** Save validation run output for analysis without re-running

**Usage:**

```bash
# Create directory (gitignored)
mkdir -p .tests

# Run validation with tee - shows output AND saves to file
node test/validate/validate.js | tee .tests/validation-$(date +%s).txt

# Run completeness verification
node test/validate/verify-completeness.js | tee .tests/completeness-$(date +%s).txt

# Analyze saved output later without re-running:
grep "TS2416" .tests/validation-*.txt
tail -50 .tests/validation-*.txt
grep "types lost" .tests/completeness-*.txt
```

**Benefits:**
- See validation output in real-time (unlike `>` redirection)
- Analyze errors without expensive re-runs (validation takes 2-3 minutes)
- Keep historical validation results for comparison
- Search across multiple validation runs

**Key Rule:** ALWAYS use `tee` for validation output, NEVER plain redirection (`>` or `2>&1`)

#### .analysis/ Directory (Research & Documentation)

**Purpose:** Keep analysis artifacts separate from source code

**Usage:**

```bash
# Create directory (gitignored)
mkdir -p .analysis

# Use for:
# - Error analysis reports
# - Type mapping investigations
# - Assembly analysis output
# - Performance profiling results
# - Architecture documentation
# - Session status reports
# - Bug fix impact analysis
```

**Benefits:**
- Keeps analysis work separate from source code
- Allows iterative analysis without cluttering repository
- Safe place for comprehensive documentation
- Gitignored - no risk of committing debug artifacts

#### .todos/ Directory (Persistent Task Tracking)

**Purpose:** Track multi-step tasks across conversation sessions

**Usage:**

```bash
# Create task file: YYYY-MM-DD-task-name.md
# Example: 2025-01-13-completeness-verification.md

# Task file must include:
# - Task overview and objectives
# - Current status (completed work)
# - Detailed remaining work list
# - Important decisions made
# - Code locations affected
# - Testing requirements
# - Special considerations

# Mark complete: YYYY-MM-DD-task-name-COMPLETED.md
```

**Benefits:**
- Resume complex tasks across sessions with full context
- No loss of progress or decisions
- Gitignored for persistence

**Note:** All directories (`.tests/`, `.analysis/`, `.todos/`) should be added to `.gitignore`

## Session Startup

### First Steps When Starting a Session

When you begin working on this project, you MUST:

1. **Read this entire CLAUDE.md file** to understand the project conventions
2. **Read STATUS.md** for current project state and metrics
3. **Read CODING-STANDARDS.md** for C# style guidelines
4. **Review recent .analysis/ reports** to understand recent work
5. **Check git status** to see uncommitted work

Only after reading these documents should you proceed with implementation tasks.

## Project Overview

**tsbindgen** is a .NET tool that generates TypeScript declaration files (.d.ts) and metadata sidecars (.metadata.json) from .NET assemblies using reflection.

### Purpose

Enable TypeScript code in the Tsonic compiler to reference .NET BCL types with full IDE support and type safety.

### Key Features

- Generates TypeScript declarations from any .NET assembly
- Creates metadata sidecars with CLR-specific information
- Handles .NET 10 BCL assemblies including System.Private.CoreLib
- Uses MetadataLoadContext for assemblies that can't be loaded normally
- Validates output with TypeScript compiler (tsc)
- Verifies completeness to ensure zero data loss through pipeline

## Architecture

### Four-Phase Pipeline

**🚨 CRITICAL: The pipeline has FOUR distinct phases! 🚨**

The generator uses a strict four-phase pipeline:

**Phase 1: Reflection** (Pure CLR domain)
- Input: .NET assembly DLL files
- Process: System.Reflection over assemblies
- Output: `AssemblySnapshot` - pure CLR metadata (no TypeScript concepts)
- Files: `*.snapshot.json` (optional debug output)
- Code: `src/tsbindgen/Reflection/Reflect.cs`

**Phase 2: Aggregation** (Pure CLR domain)
- Input: Multiple `AssemblySnapshot` files
- Process: Merge types from multiple assemblies by namespace
- Output: `NamespaceBundle` - aggregated CLR data (still no TypeScript concepts)
- Files: `namespaces/*.snapshot.json` (debug output)
- Code: `src/tsbindgen/Snapshot/Aggregate.cs`

**Phase 3: Transform** (CLR→TypeScript bridge - creates TsEmitName)
- Input: `NamespaceBundle` (CLR)
- Process:
  - `ModelTransform.Build()` - Apply name transformations via `NameTransformation.Apply()`
  - Analysis passes (covariance, diamond inheritance, explicit interfaces, etc.)
- Output: `NamespaceModel` (in-memory, has both CLR names and TS names)
- Code: `src/tsbindgen/Render/Transform/ModelTransform.cs`
- **This is where `TsEmitName` is created based on CLI options**

**Phase 4: Emit** (TypeScript domain - generates files)
- Input: `NamespaceModel` (with TsEmitName already set)
- Process:
  - `TypeScriptEmit` - Generate `.d.ts` declarations
  - `MetadataEmit` - Generate `.metadata.json`
  - `BindingEmit` - Generate `.bindings.json` (CLR→TS name mappings)
  - `TypeScriptTypeListEmit` - Generate `typelist.json` (completeness verification)
- Output: String content for files
- Write to disk:
  - `index.d.ts` - TypeScript declarations
  - `metadata.json` - CLR-specific info for Tsonic compiler
  - `bindings.json` - CLR name → TS name mappings
  - `typelist.json` - What was actually emitted (for verification)
  - `snapshot.json` - Post-transform snapshot (for verification)
- Code: `src/tsbindgen/Render/Output/*.cs`

**CRITICAL**:
- `TsEmitName` is created in **Phase 3** (Transform) using `NameTransformation.Apply()`
- Phases 1-2 use **only CLR names** (no TypeScript concepts)
- Phase 3 creates **both CLR names and TsEmitName** in models
- Phase 4 uses the **TsEmitName** from models (no further name transformation)

### Completeness Verification

The pipeline ensures **100% data integrity** through verification:

1. **snapshot.json** - What was reflected/transformed (Phase 2/3 output)
2. **typelist.json** - What was actually emitted to .d.ts (Phase 4 output)
3. **verify-completeness.js** - Compares the two to ensure zero data loss

Both files use the same flat structure with `tsEmitName` as the key (e.g., `"Delegate$InvocationListEnumerator_1"` for nested types).

### Output Files Per Namespace

Each namespace generates multiple companion files:

1. **TypeScript Declarations** (`index.d.ts`)
   - Standard TypeScript type definitions
   - Namespaces map to C# namespaces
   - Classes, interfaces, enums, delegates
   - Generic types with proper constraints
   - Branded numeric types (int, decimal, etc.)

2. **Metadata Sidecars** (`metadata.json`)
   - CLR-specific information (virtual/override, static, ref/out)
   - Used by Tsonic compiler for correct C# code generation
   - Tracks intentional omissions (indexers, generic static members)

3. **Binding Metadata** (`bindings.json`)
   - Maps TypeScript names to CLR names
   - Tracks member name transformations
   - Used for runtime binding

4. **Type List** (`typelist.json`)
   - List of all types and members actually emitted
   - Used for completeness verification
   - Flat structure matching snapshot.json

## Critical Implementation Patterns

### MetadataLoadContext Type Comparisons

**CRITICAL**: System.Reflection.MetadataLoadContext types CANNOT be compared with `typeof()`:

```csharp
// ❌ WRONG - Fails for MetadataLoadContext types
if (type == typeof(bool)) return "boolean";

// ✅ CORRECT - Use name-based comparisons
if (type.FullName == "System.Boolean") return "boolean";
```

**Why**: MetadataLoadContext loads assemblies in isolation. The `Type` objects it returns are different instances from `typeof()` results, so `==` comparisons always fail.

**Impact**: The boolean→number bug (fixed in commit dcf59e3) was caused by this exact issue.

### Type Safety Principles

**NO WEAKENING ALLOWED**: All fixes must maintain or improve type safety:

✅ **Acceptable**:
- Omitting types that can't be represented (documented in metadata)
- Using stricter types (`readonly T[]` instead of `T[]`)
- Adding method overloads for interface compatibility
- Skipping generic static members (TypeScript limitation)

❌ **NOT Acceptable**:
- Mapping all unknown types to `any`
- Removing type parameters
- Weakening return types
- Removing required properties

### Known .NET/TypeScript Impedance Mismatches

**As of v0.7.4**: The BCL compiles with **zero semantic errors** using strict TypeScript.

1. **Property Covariance** (FIXED in v0.7.4)
   - C# allows properties to return more specific types than interfaces require
   - TypeScript doesn't support property overloads (unlike methods)
   - Status: **FIXED** - PropertyOverrideUnifier uses union types for covariant properties

2. **Generic Static Members**
   - C# allows `static T DefaultValue` in `class List<T>`
   - TypeScript doesn't support this
   - Status: Intentionally skipped, tracked in metadata

3. **Indexers** (~241 instances)
   - C# indexers with different parameter types cause duplicate identifiers
   - Status: Intentionally skipped from declarations, tracked in metadata

4. **Interface Method Conflicts** (FIXED in v0.7.4)
   - Method signature mismatch between class and inherited interface
   - Status: **FIXED** - ClassPrinter emits inherited method overloads

5. **Multi-arity Constraint Propagation** (FIXED in v0.7.4)
   - Facade type parameters passed to constrained internal types
   - Status: **FIXED** - MultiArityAliasEmit uses nested constraint guards

## Type Mapping Rules (Tsonic Conventions)

### Primitive Types → Simple Aliases

```typescript
// All C# numeric types are simple number aliases
type int = number;      // System.Int32
type uint = number;     // System.UInt32
type byte = number;     // System.Byte
type decimal = number;  // System.Decimal
// etc.

// Only bool and char are branded
type bool = boolean & { __brand: "bool" };
type char = string & { __brand: "char" };

// Usage in generated code
class List_1<T> {
    readonly Count: int;  // Alias for number
}
```

Tsonic enforces numeric correctness at compile time via a proof system, not TypeScript's structural typing.

### Collections → ReadonlyArray

```csharp
// C#: IEnumerable<T>, ICollection<T>, IList<T>
// TypeScript: ReadonlyArray<T>
```

### Tasks → Promises

```csharp
// C#: Task<T>
// TypeScript: Promise<T>
```

### Nullable → Union

```csharp
// C#: int?
// TypeScript: int | null
```

### Generic Arity in Names

```typescript
// C# uses backtick: List`1
// TypeScript uses underscore: List_1
```

## Validation Workflow

### Running Validation

```bash
# Full validation (2-3 minutes)
node test/validate/validate.js

# With output capture for later analysis
node test/validate/validate.js | tee .tests/validation-$(date +%s).txt

# Run completeness verification
node test/validate/verify-completeness.js | tee .tests/completeness-$(date +%s).txt
```

### Validation Steps

1. Cleans `.tests/validation/` directory
2. Generates all 130 BCL namespaces (4,047 types)
3. Creates `index.d.ts` with triple-slash references
4. Creates `tsconfig.json`
5. Runs TypeScript compiler (`tsc`)
6. Reports error breakdown

### Completeness Verification Steps

1. Loads `snapshot.json` from each namespace (what was reflected/transformed)
2. Loads `typelist.json` from each namespace (what was emitted)
3. Compares types and members using `tsEmitName` as key
4. Filters intentional omissions (indexers, etc.)
5. Reports any data loss

### Success Criteria

- ✅ **Zero syntax errors (TS1xxx)** - All output is valid TypeScript
- ✅ **Zero semantic errors (TS2xxx)** - All type constraints satisfied (v0.7.4+)
- ✅ **All assemblies generate** - No generation failures
- ✅ **All metadata files present** - Each .d.ts has matching .metadata.json
- ✅ **100% type coverage** - All reflected types appear in typelist

### Error Categories

```
TS1xxx - Syntax errors (CRITICAL - must be zero)
TS2xxx - Semantic errors (FIXED in v0.7.4 - now zero)
TS6200 - Duplicate type aliases (expected for primitive type aliases)
```

## Common Tasks

### Generating Declarations for an Assembly

```bash
dotnet run --project src/tsbindgen/tsbindgen.csproj -- \
  generate -a /path/to/Assembly.dll \
  --out-dir output/
```

### Investigating Type Mapping Issues

1. Generate single assembly: `dotnet run -- generate -a path/to/Assembly.dll --out-dir /tmp/test`
2. Inspect output: `cat /tmp/test/Assembly/index.d.ts`
3. Check metadata: `cat /tmp/test/Assembly/metadata.json`
4. Check typelist: `cat /tmp/test/Assembly/typelist.json`
5. Validate: `npx tsc --noEmit /tmp/test/Assembly/index.d.ts`

### Analyzing Validation Errors

```bash
# Run validation with capture
node test/validate/validate.js 2>&1 | tee .tests/run.txt

# Count errors by type
grep "error TS" .tests/run.txt | sed 's/.*error \(TS[0-9]*\).*/\1/' | sort | uniq -c | sort -rn

# Find specific error examples
grep "TS2417" .tests/run.txt | head -20

# See errors for specific file
grep "System.Collections.Generic" .tests/run.txt
```

## Build Commands

```bash
# Build project
dotnet build src/tsbindgen/tsbindgen.csproj

# Run tool
dotnet run --project src/tsbindgen/tsbindgen.csproj -- <args>

# Validate all BCL assemblies
node test/validate/validate.js

# Verify completeness
node test/validate/verify-completeness.js

# Capture validation output
node test/validate/validate.js | tee .tests/validation-$(date +%s).txt

# Run all regression tests
bash test/scripts/run-all.sh
```

## Git Workflow

### Branch Strategy

1. **Work on feature branches**: `feat/feature-name` or `fix/bug-name`
2. **Commit frequently**: Small, focused commits
3. **Clear commit messages**: Follow format below
4. **Push regularly**: Keep remote in sync
5. **NEVER commit to main directly**
6. **Verify branch before commit**: `git branch --show-current`

### Commit Message Format

```
<type>: <subject>

<body>

🤖 Generated with [Claude Code](https://claude.com/claude-code)

Co-Authored-By: Claude <noreply@anthropic.com>
```

**Types**: feat, fix, docs, refactor, test, chore

**Example**:

```
fix: Use name-based type comparisons for MetadataLoadContext compatibility

Changed MapPrimitiveType() to use type.FullName comparisons instead of
typeof() because MetadataLoadContext types are different instances.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

Co-Authored-By: Claude <noreply@anthropic.com>
```

### Workflow Summary

**Critical rules (see detailed Git Safety Rules section above):**
1. ✅ **ALWAYS commit before switching contexts** - Even if work is incomplete
2. ✅ **NEVER discard uncommitted work** - Use WIP branches instead
3. ✅ **NEVER use git stash** - Use timestamped WIP branches
4. ✅ **NEVER use git reset --hard** - Use git revert for fixes
5. ✅ **Verify branch**: `git branch --show-current` before committing
6. ✅ **Push WIP branches**: Backup work on remote
7. ✅ **Use git revert not git reset** - To undo commits

**Standard workflow:**

```bash
# 1. Verify you're on correct branch
git branch --show-current

# 2. Make changes and commit frequently
git add -A
git commit -m "feat: descriptive message"

# 3. Push to remote
git push
```

## Current Status

### Metrics (as of 2025-11-08)

- **130 BCL namespaces** generated
- **4,047 types** emitted
- **Zero syntax errors** (TS1xxx)
- **12 semantic errors** (TS2417 - property covariance, expected)
- **100% type coverage** - All reflected types accounted for
- **241 indexers** intentionally omitted (tracked in metadata)

### Completeness Verification

✅ **VERIFICATION PASSED - ALL REFLECTED DATA ACCOUNTED FOR**

- Types in snapshots: 4,047
- Types in typelists: 4,047
- Members in snapshots: 75,977
- Members in typelists: 37,863 (ViewOnly and duplicate members filtered)
- Intentional omissions: 241 indexers

## When You Get Stuck

If you encounter issues:

1. **STOP immediately** - Don't implement workarounds without approval
2. **Explain the issue clearly** - Show what's blocking you
3. **Analyze root cause** - Use .analysis/ directory for investigation
4. **Propose solutions** - Suggest approaches with trade-offs
5. **Wait for user decision** - Don't proceed without explicit approval

## Key Files to Reference

- **STATUS.md** - Current project state and metrics
- **CODING-STANDARDS.md** - C# style guidelines
- **test/validate/validate.js** - BCL assembly validation script
- **test/validate/verify-completeness.js** - Completeness verification script
- **test/scripts/run-all.sh** - Run all regression tests
- **test/baselines/** - Baseline manifests for surface verification
- **.analysis/** - Analysis reports and documentation

## Remember

1. **Type safety first** - Never weaken types without approval
2. **MetadataLoadContext requires name-based comparisons** - Never use `typeof()`
3. **Validation is expensive** - Always capture output with `tee`
4. **Functional programming only** - Static classes, pure functions, immutable data
5. **Commit before switching** - Never discard uncommitted work
6. **Never use git stash** - Use WIP branches instead
7. **Ask before changing** - Get user approval for all decisions
8. **100% data integrity** - Run completeness verification to ensure zero loss
