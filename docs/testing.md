---
title: Testing
---

# Testing

Generated bindings should not be treated as correct just because generation
completed.

## Validation expectations

- verify repo-local generation results
- run compiler gates where the new bindings are used
- rerun downstreams when the changes affect real application graphs
- rerun publish preflight when versions or package contents changed

Typical real gate sequence:

1. regenerate bindings
2. run relevant repo-local tests
3. rerun `tsonic` compiler gates when call surfaces changed
4. rerun downstream applications when package graphs changed
5. run wave preflight before publishing

## What counts as repo-local testing

Depending on the repo, that can mean:

- generation scripts completing cleanly
- package-local smoke tests or selftests
- local install/overlay checks in downstream workspaces
- version-drift checks during publish preflight

## Why this is strict

Binding regressions can break:

- overload selection
- nullable and generic projection
- package metadata expectations
- downstream builds that only fail after full graph resolution

A green generator run is not enough by itself.
