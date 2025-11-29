#!/bin/bash
# Wrapper for aarch64-linux-gnu-gcc that filters out --target flag
# NativeAOT passes --target=aarch64-linux-gnu which is a clang flag,
# but the cross-compiler already knows its target.

ARGS=()
for arg in "$@"; do
  if [[ "$arg" != --target=* ]]; then
    ARGS+=("$arg")
  fi
done

exec aarch64-linux-gnu-gcc "${ARGS[@]}"
