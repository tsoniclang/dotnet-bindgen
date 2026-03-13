#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TSBINDGEN_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
TSONICLANG_ROOT="$(cd "$TSBINDGEN_ROOT/.." && pwd)"

NPM_WAVE_REPOS=(
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

NUGET_WAVE_REPOS=(
  "runtime"
  "js-runtime"
  "nodejs-clr"
  "express-clr"
)

WAVE_REPOS=(
  "${NPM_WAVE_REPOS[@]}"
  "${NUGET_WAVE_REPOS[@]}"
)

NPM_TO_PUBLISH=()
NUGET_TO_PUBLISH=()

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

  --publish  Publishes merged wave packages to npm and NuGet.
  --pub      Alias of --publish.
  pub        Alias of --publish.

Safety in --publish:
  - Every wave repo must be on clean local main
  - local main must match origin/main exactly
  - Entire wave is preflight-validated before any package is published
  - NuGet runtimes publish before npm packages that depend on them
  - Checks npm and NuGet publishables
  - Skips only when registry version matches and there is no content drift
  - Fails if local content changed at the same published version
  - Fails if local version is behind npm or NuGet

Environment:
  - NUGET_API_KEY is required if any NuGet package in the wave needs publishing.

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
      echo "Error: unknown npm repo '$repo'" >&2
      exit 1
      ;;
  esac
}

package_scope_paths_for_repo() {
  local repo="$1"
  case "$repo" in
    tsbindgen) echo "src test npm/tsbindgen package.json" ;;
    tsonic) echo "packages npm/tsonic test package.json" ;;
    core|dotnet|globals|js|nodejs|express) echo "versions/10" ;;
    aspnetcore|microsoft-extensions|efcore|efcore-sqlite|efcore-sqlserver|efcore-npgsql) echo "." ;;
    runtime) echo "Directory.Build.props src/Tsonic.Runtime" ;;
    js-runtime) echo "Directory.Build.props src/Tsonic.JSRuntime" ;;
    nodejs-clr) echo "src/nodejs" ;;
    express-clr) echo "src/express" ;;
    *)
      echo "Error: unknown repo '$repo'" >&2
      exit 1
      ;;
  esac
}

nuget_project_for_repo() {
  local repo="$1"
  case "$repo" in
    runtime) echo "src/Tsonic.Runtime/Tsonic.Runtime.csproj" ;;
    js-runtime) echo "src/Tsonic.JSRuntime/Tsonic.JSRuntime.csproj" ;;
    nodejs-clr) echo "src/nodejs/nodejs.csproj" ;;
    express-clr) echo "src/express/express.csproj" ;;
    *)
      echo "Error: unknown NuGet repo '$repo'" >&2
      exit 1
      ;;
  esac
}

xml_version_file_for_repo() {
  local repo="$1"
  case "$repo" in
    runtime|js-runtime) echo "Directory.Build.props" ;;
    nodejs-clr) echo "src/nodejs/nodejs.csproj" ;;
    express-clr) echo "src/express/express.csproj" ;;
    *)
      echo "Error: unknown NuGet repo '$repo'" >&2
      exit 1
      ;;
  esac
}

read_xml_value() {
  local file="$1"
  local tag="$2"
  sed -n "s:.*<$tag>\\([^<]*\\)</$tag>.*:\\1:p" "$file" | head -n 1
}

nuget_package_id_for_repo() {
  local repo="$1"
  local path project
  path="$(repo_path "$repo")"
  project="$(nuget_project_for_repo "$repo")"
  read_xml_value "$path/$project" "PackageId"
}

nuget_version_for_repo() {
  local repo="$1"
  local path version_file
  path="$(repo_path "$repo")"
  version_file="$(xml_version_file_for_repo "$repo")"
  read_xml_value "$path/$version_file" "Version"
}

compare_versions() {
  local v1="$1" v2="$2"
  if [ "$v1" = "$v2" ]; then
    echo 0
    return
  fi

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

last_version_bump_commit_for_npm() {
  local repo="$1"
  local package_json="$2"
  local local_version="$3"
  local path rel
  path="$(repo_path "$repo")"
  rel="${package_json#$path/}"
  git -C "$path" log -n 1 --format=%H -G "\"version\": \"$local_version\"" -- "$rel"
}

last_version_bump_commit_for_nuget() {
  local repo="$1"
  local version_file="$2"
  local local_version="$3"
  local path
  path="$(repo_path "$repo")"
  git -C "$path" log -n 1 --format=%H -G "<Version>$local_version</Version>" -- "$version_file"
}

has_content_drift_since_commit() {
  local repo="$1"
  local base_commit="$2"
  shift 2
  local path
  path="$(repo_path "$repo")"

  if [ -z "$base_commit" ]; then
    return 0
  fi

  if [ "$#" -eq 0 ]; then
    return 1
  fi

  if git -C "$path" diff --quiet "$base_commit"..HEAD -- "$@"; then
    return 1
  fi

  return 0
}

should_publish_package() {
  local repo="$1"
  local package_json="$2"
  local pkg_name local_version npm_version cmp version_commit

  pkg_name="$(node -p "require('$package_json').name")"
  local_version="$(node -p "require('$package_json').version")"
  npm_version="$(npm view "$pkg_name" version 2>/dev/null || echo "0.0.0")"
  cmp="$(compare_versions "$local_version" "$npm_version")"

  if [ "$cmp" = "-1" ]; then
    echo "Error: $pkg_name local=$local_version < npm=$npm_version"
    exit 1
  fi

  if [ "$cmp" = "0" ]; then
    version_commit="$(last_version_bump_commit_for_npm "$repo" "$package_json" "$local_version")"
    if has_content_drift_since_commit "$repo" "$version_commit" $(package_scope_paths_for_repo "$repo"); then
      echo "Error: $pkg_name has content drift since version $local_version was set, but npm already has that version."
      echo "Bump the package version first; do not silently skip this publish."
      exit 1
    fi
    echo "skip $pkg_name (already published at $local_version)"
    return 1
  fi

  echo "publish $pkg_name (local=$local_version npm=$npm_version)"
  return 0
}

nuget_latest_version() {
  local package_id="$1"
  python3 - "$package_id" <<'PY'
import json
import sys
import urllib.error
import urllib.request

package_id = sys.argv[1]
url = f"https://api.nuget.org/v3-flatcontainer/{package_id.lower()}/index.json"

try:
    with urllib.request.urlopen(url, timeout=15) as response:
        data = json.load(response)
except urllib.error.HTTPError as exc:
    if exc.code == 404:
        print("0.0.0")
        raise SystemExit(0)
    raise

versions = data.get("versions") or []
print(versions[-1] if versions else "0.0.0")
PY
}

should_publish_nuget_package() {
  local repo="$1"
  local package_id local_version remote_version cmp version_file version_commit

  package_id="$(nuget_package_id_for_repo "$repo")"
  local_version="$(nuget_version_for_repo "$repo")"
  remote_version="$(nuget_latest_version "$package_id")"
  cmp="$(compare_versions "$local_version" "$remote_version")"

  if [ "$cmp" = "-1" ]; then
    echo "Error: $package_id local=$local_version < nuget=$remote_version"
    exit 1
  fi

  if [ "$cmp" = "0" ]; then
    version_file="$(xml_version_file_for_repo "$repo")"
    version_commit="$(last_version_bump_commit_for_nuget "$repo" "$version_file" "$local_version")"
    if has_content_drift_since_commit "$repo" "$version_commit" $(package_scope_paths_for_repo "$repo"); then
      echo "Error: $package_id has content drift since version $local_version was set, but NuGet already has that version."
      echo "Bump the package version first; do not silently skip this publish."
      exit 1
    fi
    echo "skip $package_id (already published at $local_version)"
    return 1
  fi

  echo "publish $package_id (local=$local_version nuget=$remote_version)"
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
  if should_publish_package "$repo" "$package_json"; then
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
  if should_publish_package "$repo" "$package_json"; then
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
  if should_publish_package "$repo" "$package_json"; then
    echo ">>> $repo: npm publish --access public"
    (cd "$path" && npm publish --access public)
  fi
}

run_repo_publish_nuget() {
  local repo="$1"
  local path project package_id version out_dir nupkg
  path="$(repo_path "$repo")"

  assert_clean_main_latest "$repo"
  if ! should_publish_nuget_package "$repo"; then
    return
  fi

  if [ -z "${NUGET_API_KEY:-}" ]; then
    echo "Error: NUGET_API_KEY is required to publish NuGet packages in the wave."
    exit 1
  fi

  project="$(nuget_project_for_repo "$repo")"
  package_id="$(nuget_package_id_for_repo "$repo")"
  version="$(nuget_version_for_repo "$repo")"
  out_dir="$path/artifacts/nuget"
  nupkg="$out_dir/$package_id.$version.nupkg"

  echo ">>> $repo: dotnet pack $project"
  (cd "$path" && dotnet pack "$project" -c Release -o "$out_dir")

  if [ ! -f "$nupkg" ]; then
    echo "Error: expected NuGet package not found: $nupkg"
    exit 1
  fi

  echo ">>> $repo: dotnet nuget push $package_id.$version.nupkg"
  (cd "$path" && dotnet nuget push "$nupkg" --source https://api.nuget.org/v3/index.json --api-key "$NUGET_API_KEY" --skip-duplicate)
}

publish_wave() {
  echo "=== Wave publish (npm + NuGet) ==="
  echo "root: $TSONICLANG_ROOT"
  echo ""

  gather_wave_targets
  preflight_wave

  publish_nuget_wave
  publish_npm_wave

  echo ""
  echo "Wave publish complete."
}

gather_wave_targets() {
  local repo package_json

  NPM_TO_PUBLISH=()
  NUGET_TO_PUBLISH=()

  echo "=== Gather publish targets ==="

  for repo in "${NPM_WAVE_REPOS[@]}"; do
    package_json="$(package_json_for_repo "$repo")"
    assert_clean_main_latest "$repo"
    if should_publish_package "$repo" "$package_json"; then
      NPM_TO_PUBLISH+=("$repo")
    fi
  done

  for repo in "${NUGET_WAVE_REPOS[@]}"; do
    assert_clean_main_latest "$repo"
    if should_publish_nuget_package "$repo"; then
      NUGET_TO_PUBLISH+=("$repo")
    fi
  done

  echo ""
}

repo_in_array() {
  local needle="$1"
  shift
  local item
  for item in "$@"; do
    if [ "$item" = "$needle" ]; then
      return 0
    fi
  done
  return 1
}

preflight_repo() {
  local repo="$1"
  local path
  path="$(repo_path "$repo")"

  case "$repo" in
    runtime)
      echo ">>> preflight $repo: dotnet test"
      (cd "$path" && dotnet test)
      ;;
    js-runtime)
      echo ">>> preflight $repo: dotnet test"
      (cd "$path" && dotnet test)
      ;;
    nodejs-clr)
      echo ">>> preflight $repo: npm run verify:api && dotnet test -c Release"
      (cd "$path" && npm run verify:api && dotnet test -c Release)
      ;;
    express-clr)
      echo ">>> preflight $repo: dotnet test tests/express.Tests/express.Tests.csproj -c Release"
      (cd "$path" && dotnet test tests/express.Tests/express.Tests.csproj -c Release)
      ;;
    tsbindgen)
      echo ">>> preflight $repo: bash test/scripts/run-all.sh"
      (cd "$path" && bash test/scripts/run-all.sh)
      ;;
    tsonic)
      echo ">>> preflight $repo: ./test/scripts/run-all.sh"
      (cd "$path" && ./test/scripts/run-all.sh)
      ;;
    js)
      echo ">>> preflight $repo: bash scripts/selftest.sh"
      (cd "$path" && bash scripts/selftest.sh)
      ;;
    nodejs)
      echo ">>> preflight $repo: bash scripts/selftest.sh"
      (cd "$path" && bash scripts/selftest.sh)
      ;;
    express)
      echo ">>> preflight $repo: bash scripts/selftest.sh"
      (cd "$path" && bash scripts/selftest.sh)
      ;;
  esac
}

preflight_wave() {
  echo "=== Wave preflight ==="

  for repo in "${NUGET_TO_PUBLISH[@]}"; do
    preflight_repo "$repo"
  done

  local ordered_npm_preflight=(
    "tsbindgen"
    "tsonic"
    "core"
    "dotnet"
    "globals"
    "aspnetcore"
    "microsoft-extensions"
    "efcore"
    "efcore-sqlite"
    "efcore-sqlserver"
    "efcore-npgsql"
    "js"
    "nodejs"
    "express"
  )
  local repo
  for repo in "${ordered_npm_preflight[@]}"; do
    if repo_in_array "$repo" "${NPM_TO_PUBLISH[@]}"; then
      preflight_repo "$repo"
    fi
  done

  echo ""
}

publish_nuget_wave() {
  echo "=== Publish NuGet wave ==="
  local ordered_nuget=(
    "runtime"
    "js-runtime"
    "nodejs-clr"
    "express-clr"
  )
  local repo
  for repo in "${ordered_nuget[@]}"; do
    if repo_in_array "$repo" "${NUGET_TO_PUBLISH[@]}"; then
      run_repo_publish_nuget "$repo"
    fi
  done
  echo ""
}

publish_npm_wave() {
  echo "=== Publish npm wave ==="
  local ordered_npm=(
    "tsbindgen"
    "tsonic"
    "core"
    "dotnet"
    "globals"
    "aspnetcore"
    "microsoft-extensions"
    "efcore"
    "efcore-sqlite"
    "efcore-sqlserver"
    "efcore-npgsql"
    "js"
    "nodejs"
    "express"
  )
  local repo
  for repo in "${ordered_npm[@]}"; do
    if ! repo_in_array "$repo" "${NPM_TO_PUBLISH[@]}"; then
      continue
    fi
    case "$repo" in
      tsbindgen|tsonic)
        run_repo_publish_script "$repo" "$(package_json_for_repo "$repo")"
        ;;
      core|dotnet|globals|js|nodejs|express)
        run_repo_publish_npm_script "$repo" "$(package_json_for_repo "$repo")" "publish:10"
        ;;
      aspnetcore|microsoft-extensions|efcore|efcore-sqlite|efcore-sqlserver|efcore-npgsql)
        run_repo_direct_publish "$repo" "$(package_json_for_repo "$repo")"
        ;;
    esac
  done
  echo ""
}

if [ "$MODE" = "push" ]; then
  push_wave_branches
else
  publish_wave
fi
