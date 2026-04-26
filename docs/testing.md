---
title: Testing
---

# Testing

Generated bindings should not be treated as correct just because generation
completed.

## Validation expectations

- verify repo-local generation results
- run compiler gates where the new bindings are used
- rerun downstreams when generated package contents affect real application
  graphs
- rerun publish preflight when package contents require version decisions

Typical real gate sequence:

1. regenerate bindings
2. run relevant repo-local tests
3. rerun `tsonic` compiler gates for affected call surfaces
4. rerun downstream applications for affected package graphs
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

## Runtime-sensitive baselines

Surface-manifest baselines are runtime-versioned. The validator compares the
installed runtime version against the baseline metadata and reports a direct
version mismatch when they diverge.
