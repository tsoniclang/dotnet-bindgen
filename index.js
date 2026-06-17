/**
 * @tsonic/dotnet-bindgen - Programmatic API
 */

import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = dirname(fileURLToPath(import.meta.url));

const PLATFORMS = {
  "darwin-arm64": "dotnet-bindgen-darwin-arm64",
  "darwin-x64": "dotnet-bindgen-darwin-x64",
  "linux-arm64": "dotnet-bindgen-linux-arm64",
  "linux-x64": "dotnet-bindgen-linux-x64",
};

function getPlatformKey() {
  return `${process.platform}-${process.arch}`;
}

/**
 * Get the path to the dotnet-bindgen binary
 */
export function getBinaryPath() {
  const key = getPlatformKey();
  const packageName = PLATFORMS[key];

  if (!packageName) {
    throw new Error(
      `Unsupported platform: ${key}. ` +
        "dotnet-bindgen supports: darwin-arm64, darwin-x64, linux-arm64, linux-x64"
    );
  }

  const binaryName = "dotnet-bindgen";
  const paths = [
    join(__dirname, "node_modules", "@tsonic", packageName, binaryName),
    join(__dirname, "..", packageName, binaryName),
    join(__dirname, "..", "..", "@tsonic", packageName, binaryName),
  ];

  for (const p of paths) {
    if (existsSync(p)) {
      return p;
    }
  }

  throw new Error(
    `Could not find dotnet-bindgen binary for ${key}. ` +
      `Package @tsonic/${packageName} may not be installed.`
  );
}

/**
 * Run dotnet-bindgen with the given arguments
 * @param {string[]} args
 * @returns {Promise<{ code: number; stdout: string; stderr: string }>}
 */
export function run(args) {
  return new Promise((resolve, reject) => {
    const binaryPath = getBinaryPath();
    const proc = spawn(binaryPath, args);

    let stdout = "";
    let stderr = "";

    proc.stdout.on("data", (data) => {
      stdout += data.toString();
    });

    proc.stderr.on("data", (data) => {
      stderr += data.toString();
    });

    proc.on("error", reject);

    proc.on("close", (code) => {
      resolve({ code: code ?? 0, stdout, stderr });
    });
  });
}

/**
 * Check if dotnet-bindgen is available
 */
export function isAvailable() {
  try {
    getBinaryPath();
    return true;
  } catch {
    return false;
  }
}
