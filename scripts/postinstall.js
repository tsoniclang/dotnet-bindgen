#!/usr/bin/env node

/**
 * Postinstall script for @tsonic/tsbindgen
 * Links the correct platform-specific binary after npm install
 */

import { existsSync, symlinkSync, unlinkSync, chmodSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = dirname(fileURLToPath(import.meta.url));
const ROOT = join(__dirname, "..");

const PLATFORMS = {
  "darwin-arm64": "@tsonic/tsbindgen-darwin-arm64",
  "darwin-x64": "@tsonic/tsbindgen-darwin-x64",
  "linux-arm64": "@tsonic/tsbindgen-linux-arm64",
  "linux-x64": "@tsonic/tsbindgen-linux-x64",
};

const getPlatformKey = () => `${process.platform}-${process.arch}`;

const findBinary = () => {
  const key = getPlatformKey();
  const packageName = PLATFORMS[key];
  const platformDir = key; // e.g., "linux-x64"

  if (!packageName) {
    console.error(`Unsupported platform: ${key}`);
    console.error("tsbindgen supports: darwin-arm64, darwin-x64, linux-arm64, linux-x64");
    process.exit(1);
  }

  const binaryName = "tsbindgen";

  // Search paths where the platform package might be installed
  const searchPaths = [
    // Development: local npm/ directory
    join(ROOT, "npm", platformDir, binaryName),
    // Installed as dependency
    join(ROOT, "node_modules", packageName, binaryName),
    // Hoisted installations
    join(ROOT, "..", packageName, binaryName),
    join(ROOT, "..", "..", packageName, binaryName),
  ];

  for (const p of searchPaths) {
    if (existsSync(p)) {
      return p;
    }
  }

  // Not found - this is OK during development or if optional dep failed
  console.warn(`Warning: Could not find ${packageName} binary`);
  console.warn("tsbindgen will not be available on this platform");
  return null;
};

const main = () => {
  const binaryPath = findBinary();
  if (!binaryPath) {
    return;
  }

  const linkPath = join(ROOT, "tsbindgen");

  // Remove existing link if present
  if (existsSync(linkPath)) {
    unlinkSync(linkPath);
  }

  // Create symlink to platform binary
  symlinkSync(binaryPath, linkPath);
  chmodSync(linkPath, 0o755);

  console.log(`Linked tsbindgen -> ${binaryPath}`);
};

main();
