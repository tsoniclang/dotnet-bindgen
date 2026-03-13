# Agent Notes (tsbindgen)

This repo is “airplane-grade”: correctness > speed.

## Remote Safety (IMPORTANT)

- Never delete remote branches/tags, and never force-push.
- Only push new branches and open PRs; the maintainer will handle remote cleanup.

## Work Hygiene (IMPORTANT)

- Never use `git stash` (it hides work and creates dangling/unreviewed changes).
- No dangling local work: if something matters, put it on a branch as commits (and ideally push + PR it).
- Before switching tasks/repos, ensure `git status` is clean; otherwise commit to a branch or explicitly discard.

## Branch Hygiene (IMPORTANT)

- Before starting work, and again before creating a new branch, run:
  - `bash scripts/check-branch-hygiene.sh`
- Do not proceed if that script reports warnings unless the maintainer explicitly says to ignore them for the current task.
- Keep this repo on `main` unless it is the one active PR branch.
- Do not leave local feature/release branches behind after they are merged.

## Publishing Workflow (IMPORTANT)

- When asked whether publishing is needed, do not rely on version numbers alone.
- Always verify both:
  - registry version drift, and
  - content drift since the last version-bump commit for each publishable package.
- Treat `local == registry` plus content drift as a release bug that requires a version bump; do not silently skip publishing.
- For `wave-publish.sh`, “wave publish” means the full wave:
  - npm packages and NuGet packages
  - no implicit omission of runtime packages.
