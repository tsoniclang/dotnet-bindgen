#!/bin/bash
# Regression: non-generic Task/ValueTask must be awaitable as `void`
# (i.e., include `then(value: void)` overload so Awaited<Task> becomes void).
#
# Generic Task<TResult>/ValueTask<TResult> must NOT include void overload (Awaited<> would infer never).
#
# Additional requirement (CLR faithfulness): Task<TResult> : Task, so Task_1<TResult> must remain
# assignable to Task in TypeScript. We achieve that by including an extra
# `then(value: TResult | void)` compatibility overload on generic Task_1 only
# (NOT on ValueTask_1, which has no inheritance relation in the CLR).

set -euo pipefail

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "================================================"
echo "Thenable Task/ValueTask Regression Test"
echo "================================================"
echo ""

BCL_DIR="$(ensure_bcl default)"
FILE="$BCL_DIR/System.Threading.Tasks/internal/index.d.ts"

if [ ! -f "$FILE" ]; then
    echo -e "${RED}❌ FAILED: Missing BCL file: $FILE${NC}" >&2
    exit 1
fi

echo "Using BCL: $BCL_DIR"
echo "File: $FILE"
echo ""

# Non-generic Task / ValueTask must include void + unknown overloads.
assert_grep 'export type Task = Task$instance & __Task$views & {' "$FILE" "Task type alias exists"
assert_grep "then<TResult1 = void" "$FILE" "Task includes void then overload"
assert_grep "then<TResult1 = unknown" "$FILE" "Task includes unknown then overload"

assert_grep 'export type ValueTask = ValueTask$instance & __ValueTask$views & {' "$FILE" "ValueTask type alias exists"
assert_grep "then<TResult1 = void" "$FILE" "ValueTask includes void then overload"
assert_grep "then<TResult1 = unknown" "$FILE" "ValueTask includes unknown then overload"

# Generic forms must not include a void overload (Awaited<Task_1<T>> should not collapse to never).
task1_start="$(grep -n "export type Task_1" "$FILE" | head -1 | cut -d: -f1 || true)"
if [ -n "$task1_start" ]; then
    task1_block="$(sed -n "${task1_start},$((task1_start + 12))p" "$FILE")"
    if echo "$task1_block" | grep -Fq "then<TResult1 = void"; then
        echo -e "${RED}[FAIL]${NC} Task_1<TResult> must not include void then overload" >&2
        echo "$task1_block" >&2
        exit 1
    fi
    test_result PASS "Task_1<TResult> does not include void then overload"
    if ! echo "$task1_block" | grep -Fq "value: TResult | void"; then
        echo -e "${RED}[FAIL]${NC} Task_1<TResult> must include a TResult | void compatibility overload for Task<TResult> : Task assignability" >&2
        echo "$task1_block" >&2
        exit 1
    fi
    test_result PASS "Task_1<TResult> includes TResult | void compatibility overload (Task<TResult> : Task)"
else
    echo -e "${RED}❌ FAILED: Could not locate Task_1 alias${NC}" >&2
    exit 1
fi

vt1_start="$(grep -n "export type ValueTask_1" "$FILE" | head -1 | cut -d: -f1 || true)"
if [ -n "$vt1_start" ]; then
    vt1_block="$(sed -n "${vt1_start},$((vt1_start + 12))p" "$FILE")"
    if echo "$vt1_block" | grep -Fq "then<TResult1 = void"; then
        echo -e "${RED}[FAIL]${NC} ValueTask_1<TResult> must not include void then overload" >&2
        echo "$vt1_block" >&2
        exit 1
    fi
    test_result PASS "ValueTask_1<TResult> does not include void then overload"
else
    echo -e "${RED}❌ FAILED: Could not locate ValueTask_1 alias${NC}" >&2
    exit 1
fi

echo ""
echo "================================================"
echo -e "${GREEN}✓ THENABLE TASK/VALUE_TASK TEST PASSED${NC}"
echo "================================================"
echo ""
