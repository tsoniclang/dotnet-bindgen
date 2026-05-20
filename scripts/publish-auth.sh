#!/usr/bin/env bash

NPM_REGISTRY_URL="${NPM_REGISTRY_URL:-https://registry.npmjs.org/}"
NUGET_VERIFY_KEY_BASE_URL="${NUGET_VERIFY_KEY_BASE_URL:-https://www.nuget.org/api/v2/package/create-verification-key}"
PUBLISH_AUTH_NPM_CONFIG_PATH="${PUBLISH_AUTH_NPM_CONFIG_PATH:-}"
PUBLISH_AUTH_CLEANUP_REGISTERED="${PUBLISH_AUTH_CLEANUP_REGISTERED:-false}"

publish_auth_cleanup() {
  if [ -n "$PUBLISH_AUTH_NPM_CONFIG_PATH" ]; then
    rm -f "$PUBLISH_AUTH_NPM_CONFIG_PATH"
  fi
}

publish_auth_register_cleanup() {
  if [ "$PUBLISH_AUTH_CLEANUP_REGISTERED" != "true" ]; then
    trap publish_auth_cleanup EXIT
    PUBLISH_AUTH_CLEANUP_REGISTERED=true
  fi
}

publish_auth_fail() {
  echo "Error: $*" >&2
  exit 1
}

publish_auth_write_npm_config() {
  local config_path="$1"
  umask 077
  printf 'registry=%s\n//registry.npmjs.org/:_authToken=%s\n' "$NPM_REGISTRY_URL" "$NPM_TOKEN" >"$config_path"
}

publish_auth_activate_npm_token() {
  publish_auth_register_cleanup
  if [ -n "$PUBLISH_AUTH_NPM_CONFIG_PATH" ]; then
    rm -f "$PUBLISH_AUTH_NPM_CONFIG_PATH"
  fi
  PUBLISH_AUTH_NPM_CONFIG_PATH="$(mktemp "${TMPDIR:-/tmp}/tsonic-publish-npmrc.XXXXXX")"
  publish_auth_write_npm_config "$PUBLISH_AUTH_NPM_CONFIG_PATH"
  export NPM_CONFIG_USERCONFIG="$PUBLISH_AUTH_NPM_CONFIG_PATH"
}

ensure_npm_publish_auth() {
  if [ "$#" -eq 0 ]; then
    return 0
  fi

  echo "=== Validate npm publish auth ==="

  if [ -z "${NPM_TOKEN:-}" ]; then
    publish_auth_fail "NPM_TOKEN is required before running publish tests. npm login is not enough for non-interactive publish when 2FA is enforced."
  fi

  local auth_config token_user token_list token_list_err
  auth_config="$(mktemp "${TMPDIR:-/tmp}/tsonic-publish-auth-check.XXXXXX")"
  publish_auth_write_npm_config "$auth_config"

  if ! token_user="$(NPM_CONFIG_USERCONFIG="$auth_config" npm whoami --registry="$NPM_REGISTRY_URL" 2>&1)"; then
    rm -f "$auth_config"
    publish_auth_fail "NPM_TOKEN is not accepted by npm whoami; fix the token before running expensive tests. npm said: $token_user"
  fi

  rm -f "$auth_config"

  token_list="$(mktemp "${TMPDIR:-/tmp}/tsonic-npm-tokens.XXXXXX")"
  token_list_err="$(mktemp "${TMPDIR:-/tmp}/tsonic-npm-tokens.err.XXXXXX")"
  if ! npm token list --json --registry="$NPM_REGISTRY_URL" >"$token_list" 2>"$token_list_err"; then
    local error_text
    error_text="$(cat "$token_list_err")"
    rm -f "$token_list" "$token_list_err"
    publish_auth_fail "could not verify that NPM_TOKEN has package write + bypass_2fa before publish. npm token list failed: $error_text"
  fi

  NPM_PUBLISH_USER="$token_user" node - "$token_list" "$@" <<'NODE'
const fs = require("node:fs");

const tokenListPath = process.argv[2];
const packageNames = process.argv.slice(3);
const token = process.env.NPM_TOKEN || "";
const tokens = JSON.parse(fs.readFileSync(tokenListPath, "utf8"));
const now = Date.now();

const tokenMatches = (visibleToken) => {
  if (typeof visibleToken !== "string" || visibleToken.length === 0) {
    return false;
  }

  if (!visibleToken.includes("...")) {
    return visibleToken === token;
  }

  const [prefix, suffix] = visibleToken.split("...");
  return token.startsWith(prefix) && token.endsWith(suffix);
};

const scopeCoversPackage = (scope, packageName) => {
  if (!scope || typeof scope !== "object") {
    return false;
  }

  const scopeName = scope.name;
  if (scopeName === null || scopeName === undefined || scopeName === "*") {
    return true;
  }

  if (scopeName === packageName) {
    return true;
  }

  return typeof scopeName === "string" &&
    scopeName.startsWith("@") &&
    packageName.startsWith(`${scopeName}/`);
};

const matchingToken = tokens.find((entry) => tokenMatches(entry.token));

if (!matchingToken) {
  console.error("NPM_TOKEN is accepted by npm, but npm token list cannot identify it.");
  console.error("A non-interactive release needs a visible granular/automation token so bypass_2fa can be verified before tests run.");
  process.exit(1);
}

const expiryTime = matchingToken.expiry ? Date.parse(matchingToken.expiry) : Number.NaN;
const revoked = Boolean(matchingToken.revoked);
const expired = Number.isFinite(expiryTime) && expiryTime <= now;
const permissions = Array.isArray(matchingToken.permissions) ? matchingToken.permissions : [];
const hasPackageWrite = permissions.some(
  (permission) => permission &&
    permission.name === "package" &&
    permission.action === "write"
);
const legacyWritable =
  matchingToken.readonly === false ||
  matchingToken.automation === true ||
  String(matchingToken.type || "").toLowerCase() === "automation";
const canWritePackages = hasPackageWrite || legacyWritable;
const bypassesTwoFactor =
  matchingToken.bypass_2fa === true ||
  matchingToken.automation === true ||
  String(matchingToken.type || "").toLowerCase() === "automation";
const scopes = Array.isArray(matchingToken.scopes) ? matchingToken.scopes : [];
const missingScopes =
  scopes.length === 0
    ? []
    : packageNames.filter(
        (packageName) => !scopes.some((scope) => scopeCoversPackage(scope, packageName))
      );

const failures = [];
if (revoked) failures.push("token is revoked");
if (expired) failures.push(`token expired at ${matchingToken.expiry}`);
if (!canWritePackages) failures.push("token lacks package write permission");
if (!bypassesTwoFactor) failures.push("token does not have bypass_2fa/automation publish capability");
if (missingScopes.length > 0) {
  failures.push(`token scope does not cover: ${missingScopes.join(", ")}`);
}

if (failures.length > 0) {
  console.error("NPM_TOKEN is not publish-ready:");
  for (const failure of failures) console.error(`  - ${failure}`);
  process.exit(1);
}

console.log(`npm publish auth ready for ${process.env.NPM_PUBLISH_USER}`);
NODE
  local node_status=$?
  rm -f "$token_list" "$token_list_err"
  if [ "$node_status" -ne 0 ]; then
    exit "$node_status"
  fi

  publish_auth_activate_npm_token
}

ensure_nuget_publish_auth() {
  if [ "$#" -eq 0 ]; then
    return 0
  fi

  echo "=== Validate NuGet publish auth ==="

  if [ -z "${NUGET_API_KEY:-}" ]; then
    publish_auth_fail "NUGET_API_KEY is required before running publish tests."
  fi

  python3 - "$@" <<'PY'
import os
import sys
import urllib.error
import urllib.parse
import urllib.request

api_key = os.environ["NUGET_API_KEY"]
base_url = os.environ.get(
    "NUGET_VERIFY_KEY_BASE_URL",
    "https://www.nuget.org/api/v2/package/create-verification-key",
)

for package_id in sys.argv[1:]:
    url = f"{base_url}/{urllib.parse.quote(package_id, safe='')}"
    request = urllib.request.Request(
        url,
        method="POST",
        headers={
            "X-NuGet-ApiKey": api_key,
            "X-NuGet-Protocol-Version": "4.1.0",
        },
    )
    try:
        with urllib.request.urlopen(request, timeout=15) as response:
            response.read()
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")
        print(
            f"Error: NUGET_API_KEY is not publish-ready for {package_id}: "
            f"HTTP {exc.code} {detail}",
            file=sys.stderr,
        )
        raise SystemExit(1)
    except urllib.error.URLError as exc:
        print(
            f"Error: could not verify NUGET_API_KEY for {package_id}: {exc}",
            file=sys.stderr,
        )
        raise SystemExit(1)

print("NuGet publish auth ready")
PY
}
