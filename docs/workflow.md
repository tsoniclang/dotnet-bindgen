---
title: Workflow
---

# Workflow and Publish Discipline

`dotnet-bindgen` participates in release waves, not just one-off generation.

## Normal workflow

1. regenerate the affected binding repos
2. run the relevant repo-local checks
3. rerun compiler gates for affected call surfaces or bindings metadata
4. rerun downstream applications for affected real programs
5. check version drift
6. publish the coherent wave

## Why the workflow is strict

Generated binding work can break:

- overload selection
- nullable and generic projection
- package metadata expectations
- downstream application builds

It can also fail only after:

- `tsonic` E2E fixtures
- downstream application startup
- publish-time version checks
- cross-package import ownership checks

That is why regen and publish are part of a verified wave.

## Repo-local scripts matter

In the generated binding repos, prefer the repo-local scripts over ad hoc manual
publishing. The stack uses:

- repo-local generation scripts for each binding family
- `dotnet-bindgen` test suites for generator work
- `dotnet-bindgen/scripts/wave-publish.sh` for release-wave preflight and publish of
  npm packages and NuGet runtime packages

Those scripts enforce the release rules:

- clean `main`
- latest `origin/main`
- no same-version silent republish
- preflight across the whole wave before publishing any package
- content-drift checks since the last version-bump commit
- NuGet runtime publishing before npm packages that depend on runtime behavior

## Relationship to first-party source packages

Generated CLR bindings and first-party source packages live side by side, but
they are not owned the same way:

- `dotnet-bindgen` owns the generated binding packages
- `js`, `nodejs`, and `express` own their own authored TypeScript source

## Practical consequence

If you are debugging:

- a CLR namespace import problem -> start in `dotnet-bindgen` or the generated repo
- a first-party authored package issue -> start in `js`, `nodejs`, or `express`
- a mixed wave failure -> verify the generated packages and downstreams together

## Wave publish scope

The release wave includes:

- npm packages: `dotnet-bindgen`, `tsonic`, `core`, `dotnet`, `globals`, `js`,
  `nodejs`, `express`, `aspnetcore`, `microsoft-extensions`, `efcore`,
  `efcore-sqlite`, `efcore-sqlserver`, and `efcore-npgsql`
- NuGet packages: `runtime`

`--push` pushes non-main wave branches and prints PR URLs. `--publish` requires
clean, latest `main` in every wave repo and validates the entire wave before any
registry write.
