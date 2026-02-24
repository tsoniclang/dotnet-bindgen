#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TSBINDGEN_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
TSONICLANG_ROOT="$(cd "$TSBINDGEN_ROOT/.." && pwd)"

WAVE_REPOS=(
  "tsbindgen"
  "tsonic"
  "core"
  "dotnet"
  "globals"
  "js"
  "nodejs"
  "express"
  "aspnetcore"
  "microsoft-extensions"
  "efcore"
  "efcore-sqlite"
  "efcore-sqlserver"
  "efcore-npgsql"
)

usage() {
  cat <<'EOF'
Usage:
  ./scripts/wave-publish.sh --push
  ./scripts/wave-publish.sh --publish
  ./scripts/wave-publish.sh --pub
  ./scripts/wave-publish.sh pub

Modes:
  --push     Pushes current non-main wave branches and prints PR URLs.
             (Use this before merge.)

  --publish  Publishes merged wave packages to npm.
  --pub      Alias of --publish.
  pub        Alias of --publish.

Safety in --publish:
  - Every wave repo must be on clean local main
  - local main must match origin/main exactly
  - Skips already-published versions
  - Fails if local version is behind npm

No args:
  Prints this help and exits.
EOF
}

if [ "$#" -eq 0 ]; then
  usage
  exit 0
fi

if [ "$#" -ne 1 ]; then
  echo "Error: expected exactly one argument."
  usage
  exit 1
fi

MODE=""
case "$1" in
  --push)
    MODE="push"
    ;;
  --publish|--pub|pub)
    MODE="publish"
    ;;
  -h|--help)
    usage
    exit 0
    ;;
  *)
    echo "Error: unknown argument '$1'"
    usage
    exit 1
    ;;
esac

repo_path() {
  local repo="$1"
  echo "$TSONICLANG_ROOT/$repo"
}

package_json_for_repo() {
  local repo="$1"
  case "$repo" in
    tsbindgen) echo "$TSONICLANG_ROOT/tsbindgen/package.json" ;;
    tsonic) echo "$TSONICLANG_ROOT/tsonic/packages/cli/package.json" ;;
    core|dotnet|globals|js|nodejs|express) echo "$TSONICLANG_ROOT/$repo/versions/10/package.json" ;;
    aspnetcore|microsoft-extensions|efcore|efcore-sqlite|efcore-sqlserver|efcore-npgsql) echo "$TSONICLANG_ROOT/$repo/package.json" ;;
    *)
      echo "Error: unknown repo '$repo'" >&2
      exit 1
      ;;
  esac
}

compare_versions() {
  local v1="$1" v2="$2"
  if [ "$v1" = "$v2" ]; then echo 0; return; fi

  IFS='.' read -r v1_major v1_minor v1_patch <<<"$v1"
  IFS='.' read -r v2_major v2_minor v2_patch <<<"$v2"

  if [ "$v1_major" -gt "$v2_major" ]; then echo 1; return; fi
  if [ "$v1_major" -lt "$v2_major" ]; then echo -1; return; fi
  if [ "$v1_minor" -gt "$v2_minor" ]; then echo 1; return; fi
  if [ "$v1_minor" -lt "$v2_minor" ]; then echo -1; return; fi
  if [ "$v1_patch" -gt "$v2_patch" ]; then echo 1; return; fi
  if [ "$v1_patch" -lt "$v2_patch" ]; then echo -1; return; fi
  echo 0
}

assert_clean_main_latest() {
  local repo="$1"
  local path
  path="$(repo_path "$repo")"

  if [ ! -d "$path/.git" ]; then
    echo "Error: missing git repo: $path"
    exit 1
  fi

  local branch
  branch="$(git -C "$path" branch --show-current)"
  if [ "$branch" != "main" ]; then
    echo "Error: $repo is on '$branch' (expected 'main' for --publish)."
    exit 1
  fi

  if [ -n "$(git -C "$path" status --porcelain)" ]; then
    echo "Error: $repo has uncommitted changes."
    exit 1
  fi

  git -C "$path" fetch origin main >/dev/null
  local local_commit remote_commit
  local_commit="$(git -C "$path" rev-parse HEAD)"
  remote_commit="$(git -C "$path" rev-parse origin/main)"
  if [ "$local_commit" != "$remote_commit" ]; then
    echo "Error: $repo local main is not latest origin/main."
    exit 1
  fi
}

should_publish_package() {
  local package_json="$1"
  local pkg_name local_version npm_version cmp

  pkg_name="$(node -p "require('$package_json').name")"
  local_version="$(node -p "require('$package_json').version")"
  npm_version="$(npm view "$pkg_name" version 2>/dev/null || echo "0.0.0")"
  cmp="$(compare_versions "$local_version" "$npm_version")"

  if [ "$cmp" = "-1" ]; then
    echo "Error: $pkg_name local=$local_version < npm=$npm_version"
    exit 1
  fi

  if [ "$cmp" = "0" ]; then
    echo "skip $pkg_name (already published at $local_version)"
    return 1
  fi

  echo "publish $pkg_name (local=$local_version npm=$npm_version)"
  return 0
}

push_wave_branches() {
  local pushed=0
  echo "=== Wave push ==="
  echo ""

  for repo in "${WAVE_REPOS[@]}"; do
    local path branch counts ahead behind
    path="$(repo_path "$repo")"
    if [ ! -d "$path/.git" ]; then
      echo "skip $repo (repo missing)"
      continue
    fi

    branch="$(git -C "$path" branch --show-current)"
    if [ "$branch" = "main" ]; then
      echo "skip $repo (on main)"
      continue
    fi

    if [ -n "$(git -C "$path" status --porcelain)" ]; then
      echo "Error: $repo has uncommitted changes on branch '$branch'."
      exit 1
    fi

    git -C "$path" fetch origin main >/dev/null
    counts="$(git -C "$path" rev-list --left-right --count origin/main..."$branch" 2>/dev/null || echo "0 0")"
    behind="$(echo "$counts" | awk '{print $1}')"
    ahead="$(echo "$counts" | awk '{print $2}')"

    if [ "$ahead" -eq 0 ]; then
      echo "skip $repo (branch '$branch' has no commits ahead of main)"
      continue
    fi

    if git -C "$path" rev-parse --abbrev-ref --symbolic-full-name "@{u}" >/dev/null 2>&1; then
      git -C "$path" push
    else
      git -C "$path" push -u origin "$branch"
    fi

    pushed=$((pushed + 1))
    echo "PR: https://github.com/tsoniclang/$repo/pull/new/$branch"
    if [ "$behind" -gt 0 ]; then
      echo "note: $repo/$branch is $behind behind main"
    fi
    echo ""
  done

  if [ "$pushed" -eq 0 ]; then
    echo "No non-main wave branches were pushed."
  else
    echo "Pushed $pushed wave branch(es)."
  fi
}

run_repo_publish_script() {
  local repo="$1"
  local package_json="$2"
  local path
  path="$(repo_path "$repo")"

  assert_clean_main_latest "$repo"
  if should_publish_package "$package_json"; then
    echo ">>> $repo: scripts/publish-npm.sh"
    (cd "$path" && ./scripts/publish-npm.sh)
  fi
}

run_repo_publish_npm_script() {
  local repo="$1"
  local package_json="$2"
  local npm_script="$3"
  local path
  path="$(repo_path "$repo")"

  assert_clean_main_latest "$repo"
  if should_publish_package "$package_json"; then
    echo ">>> $repo: npm run $npm_script"
    (cd "$path" && npm run "$npm_script")
  fi
}

run_repo_direct_publish() {
  local repo="$1"
  local package_json="$2"
  local path
  path="$(repo_path "$repo")"

  assert_clean_main_latest "$repo"
  if should_publish_package "$package_json"; then
    echo ">>> $repo: npm publish --access public"
    (cd "$path" && npm publish --access public)
  fi
}

publish_wave() {
  echo "=== Wave publish (npm) ==="
  echo "root: $TSONICLANG_ROOT"
  echo ""

  run_repo_publish_script "tsbindgen" "$(package_json_for_repo tsbindgen)"
  run_repo_publish_script "tsonic" "$(package_json_for_repo tsonic)"

  run_repo_publish_npm_script "core" "$(package_json_for_repo core)" "publish:10"
  run_repo_publish_npm_script "dotnet" "$(package_json_for_repo dotnet)" "publish:10"
  run_repo_publish_npm_script "globals" "$(package_json_for_repo globals)" "publish:10"
  run_repo_publish_npm_script "js" "$(package_json_for_repo js)" "publish:10"
  run_repo_publish_npm_script "nodejs" "$(package_json_for_repo nodejs)" "publish:10"
  run_repo_publish_npm_script "express" "$(package_json_for_repo express)" "publish:10"

  run_repo_direct_publish "aspnetcore" "$(package_json_for_repo aspnetcore)"
  run_repo_direct_publish "microsoft-extensions" "$(package_json_for_repo microsoft-extensions)"
  run_repo_direct_publish "efcore" "$(package_json_for_repo efcore)"
  run_repo_direct_publish "efcore-sqlite" "$(package_json_for_repo efcore-sqlite)"
  run_repo_direct_publish "efcore-sqlserver" "$(package_json_for_repo efcore-sqlserver)"
  run_repo_direct_publish "efcore-npgsql" "$(package_json_for_repo efcore-npgsql)"

  echo ""
  echo "Wave publish complete."
}

if [ "$MODE" = "push" ]; then
  push_wave_branches
else
  publish_wave
fi
