---
title: Workflow
---

# Workflow and Publish Discipline

`tsbindgen` participates in release waves, not just one-off generation.

## Normal workflow

1. regenerate the affected binding repos
2. run the relevant repo-local checks
3. rerun compiler gates when call surfaces or bindings metadata changed
4. rerun downstream applications when the change can affect real programs
5. check version drift
6. publish the coherent wave

## Why the workflow is strict

A generated binding change can break:

- overload selection
- nullable and generic projection
- package metadata expectations
- downstream application builds

It can also fail only after:

- `tsonic` E2E fixtures
- downstream application startup
- publish-time version checks
- cross-package import ownership checks

That is why the stack now treats regen and publish as part of a verified wave.

## Repo-local scripts matter

In the generated binding repos, prefer the repo-local scripts over ad hoc manual
publishing. In the current stack that usually means:

- repo-local generation scripts for each binding family
- `tsbindgen` test suites for generator changes
- `tsbindgen/scripts/wave-publish.sh` for release-wave preflight and publish

Those scripts enforce the current release rules:

- clean `main`
- latest `origin/main`
- no same-version silent republish
- preflight across the whole wave before publishing any package

## Relationship to first-party source packages

Generated CLR bindings and first-party source packages live side by side, but
they are not owned the same way:

- `tsbindgen` owns the generated binding packages
- `js`, `nodejs`, and `express` own their own authored TypeScript source

## Practical consequence

If you are debugging:

- a CLR namespace import problem -> start in `tsbindgen` or the generated repo
- a first-party authored package issue -> start in `js`, `nodejs`, or `express`
- a mixed wave failure -> verify the generated packages and downstreams together
