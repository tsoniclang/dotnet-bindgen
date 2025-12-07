#!/usr/bin/env node

import { existsSync, mkdirSync, symlinkSync, unlinkSync, chmodSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = dirname(fileURLToPath(import.meta.url));

const PLATFORMS = {
  "darwin-arm64": "tsbindgen-darwin-arm64",
  "darwin-x64": "tsbindgen-darwin-x64",
  "linux-arm64": "tsbindgen-linux-arm64",
  "linux-x64": "tsbindgen-linux-x64",
};

const getPlatformKey = () => `${process.platform}-${process.arch}`;

const findBinary = () => {
  const key = getPlatformKey();
  const packageName = PLATFORMS[key];

  if (!packageName) {
    console.error(`tsbindgen: Unsupported platform: ${key}`);
    console.error("Supported: darwin-arm64, darwin-x64, linux-arm64, linux-x64");
    process.exit(1);
  }

  const binaryName = "tsbindgen";

  // Search paths for the platform-specific binary
  const paths = [
    // Nested in this package's node_modules
    join(__dirname, "node_modules", "@tsonic", packageName, binaryName),
    // Sibling in @tsonic scope (hoisted)
    join(__dirname, "..", packageName, binaryName),
    // At root node_modules level
    join(__dirname, "..", "..", "@tsonic", packageName, binaryName),
  ];

  for (const p of paths) {
    if (existsSync(p)) {
      return p;
    }
  }

  console.error(`tsbindgen: Could not find binary for ${key}`);
  console.error(`Package @tsonic/${packageName} may not be installed.`);
  process.exit(1);
};

// Create bin directory if needed
const binDir = join(__dirname, "bin");
if (!existsSync(binDir)) {
  mkdirSync(binDir);
}

// Find the platform binary
const binaryPath = findBinary();

// Create symlink
const symlinkPath = join(binDir, "tsbindgen");

// Remove existing symlink if present
if (existsSync(symlinkPath)) {
  unlinkSync(symlinkPath);
}

symlinkSync(binaryPath, symlinkPath);
chmodSync(symlinkPath, 0o755);

console.log(`tsbindgen: Linked to ${binaryPath}`);
