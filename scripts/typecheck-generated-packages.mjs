import { spawn } from "node:child_process";
import { appendFileSync, copyFileSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptPath = fileURLToPath(import.meta.url);
const repoRoot = path.resolve(path.dirname(scriptPath), "..");
const workspaceRoot = path.resolve(repoRoot, "..");
const scratchRoot = path.join(repoRoot, ".temp", "generated-package-typecheck");

const packageRoots = [
  path.join(workspaceRoot, "core"),
  path.join(workspaceRoot, "dotnet", "versions", "10"),
  path.join(workspaceRoot, "microsoft-extensions"),
  path.join(workspaceRoot, "aspnetcore"),
  path.join(workspaceRoot, "efcore"),
  path.join(workspaceRoot, "efcore-sqlite"),
  path.join(workspaceRoot, "efcore-sqlserver"),
  path.join(workspaceRoot, "efcore-npgsql"),
];

function readJson(filePath) {
  return JSON.parse(readFileSync(filePath, "utf8"));
}

function walkDeclarations(root) {
  const files = [];
  const stack = [root];

  while (stack.length > 0) {
    const current = stack.pop();
    for (const entry of readdirSync(current, { withFileTypes: true })) {
      if (entry.name === ".git" || entry.name === "node_modules" || entry.name === ".temp") {
        continue;
      }

      const fullPath = path.join(current, entry.name);
      if (entry.isDirectory()) {
        stack.push(fullPath);
      } else if (entry.isFile() && entry.name.endsWith(".d.ts")) {
        files.push(fullPath);
      }
    }
  }

  return files.sort();
}

function packageImportPath(packageName, root, declarationPath) {
  const rel = path.relative(root, declarationPath).replaceAll(path.sep, "/");
  if (rel.endsWith(".js.d.ts")) {
    return `${packageName}/${rel.slice(0, -".d.ts".length)}`;
  }

  if (rel.endsWith(".d.ts")) {
    return `${packageName}/${rel.slice(0, -".d.ts".length)}.js`;
  }

  throw new Error(`Unsupported declaration path: ${declarationPath}`);
}

function exportedImportPaths(packageName, root, packageJson) {
  if (!packageJson.exports) {
    return walkDeclarations(root).map((file) => packageImportPath(packageName, root, file));
  }

  return Object.keys(packageJson.exports)
    .filter((subpath) => subpath !== ".")
    .map((subpath) => `${packageName}/${subpath.slice(2)}`)
    .sort();
}

const excludedPackageCopyDirectories = new Set([".git", ".temp", ".tests", "__build", "node_modules"]);

function copyPackageForTypecheck(sourceRoot, targetRoot) {
  mkdirSync(targetRoot, { recursive: true });

  for (const entry of readdirSync(sourceRoot, { withFileTypes: true })) {
    if (entry.isDirectory() && excludedPackageCopyDirectories.has(entry.name)) {
      continue;
    }

    const sourcePath = path.join(sourceRoot, entry.name);
    const targetPath = path.join(targetRoot, entry.name);

    if (entry.isDirectory()) {
      copyPackageForTypecheck(sourcePath, targetPath);
    } else if (entry.isFile()) {
      mkdirSync(path.dirname(targetPath), { recursive: true });
      copyFileSync(sourcePath, targetPath);
    }
  }
}

function resolveTsgoBin() {
  if (process.env.TSGO_BIN) {
    return process.env.TSGO_BIN;
  }

  return path.join(repoRoot, "node_modules", ".bin", process.platform === "win32" ? "tsgo.cmd" : "tsgo");
}

const timestamp = new Date().toISOString().replaceAll(/[-:.TZ]/g, "").slice(0, 14);
const workDir = path.join(scratchRoot, timestamp);
const nodeModulesAtTsonic = path.join(workDir, "node_modules", "@tsonic");
mkdirSync(nodeModulesAtTsonic, { recursive: true });
const packageFilter = new Set(
  (process.env.TSGO_TYPECHECK_PACKAGE_FILTER ?? "")
    .split(",")
    .map((entry) => entry.trim())
    .filter(Boolean)
);

const packageSummaries = [];
const packageInfos = [];

for (const root of packageRoots) {
  const packageJson = readJson(path.join(root, "package.json"));
  const packageName = packageJson.name;
  const packageDirName = packageName.split("/").pop();
  const selected = packageFilter.size === 0 || packageFilter.has(packageName);
  const declarations = walkDeclarations(root);
  const imports = exportedImportPaths(packageName, root, packageJson);

  const scratchPackageRoot = path.join(nodeModulesAtTsonic, packageDirName);
  copyPackageForTypecheck(root, scratchPackageRoot);

  const rootFiles = declarations.map((declaration) =>
    path.relative(workDir, path.join(scratchPackageRoot, path.relative(root, declaration))).replaceAll(path.sep, "/")
  );

  packageSummaries.push({
    name: packageName,
    version: packageJson.version,
    declarations: declarations.length,
    imports: imports.length,
  });

  packageInfos.push({
    name: packageName,
    version: packageJson.version,
    packageDirName,
    selected,
    rootFiles,
    imports,
  });
}

writeFileSync(path.join(workDir, "package.json"), `${JSON.stringify({ private: true, type: "module" }, null, 2)}\n`);

const compilerOptions = {
  target: "ES2022",
  module: "NodeNext",
  moduleResolution: "NodeNext",
  preserveSymlinks: true,
  strict: true,
  skipLibCheck: false,
  noEmit: true,
  types: [],
};

function parsePositiveInt(value, fallback) {
  const parsed = Number.parseInt(value ?? "", 10);
  return Number.isInteger(parsed) && parsed > 0 ? parsed : fallback;
}

function chunks(items, size) {
  const result = [];
  for (let index = 0; index < items.length; index += size) {
    result.push(items.slice(index, index + size));
  }
  return result;
}

function writeShardTsconfig(checkDir, files) {
  writeFileSync(
    path.join(checkDir, "tsconfig.json"),
    `${JSON.stringify(
      {
        compilerOptions,
        files: files.map((file) => path.relative(checkDir, file).replaceAll(path.sep, "/")),
      },
      null,
      2
    )}\n`
  );
}

const declarationShardSize = parsePositiveInt(process.env.TSGO_TYPECHECK_DECLARATION_SHARD_SIZE, 8);
const importShardSize = parsePositiveInt(process.env.TSGO_TYPECHECK_IMPORT_SHARD_SIZE, 16);
const shardTimeoutMs = parsePositiveInt(process.env.TSGO_TYPECHECK_SHARD_TIMEOUT_MS, 10 * 60_000);

let totalImports = 0;
let totalRootFiles = 0;
const manifestLines = [];
manifestLines.push(`scratch: ${workDir}`);
manifestLines.push("");
manifestLines.push("Package checks:");

const checks = [];

for (const info of packageInfos) {
  if (!info.selected) {
    continue;
  }

  const importGroups = chunks(info.imports, importShardSize);
  const declarationGroups = chunks(info.rootFiles, declarationShardSize);

  for (let shardIndex = 0; shardIndex < importGroups.length; shardIndex++) {
    const imports = importGroups[shardIndex];
    const checkDir = path.join(workDir, "checks", info.packageDirName, `imports-${shardIndex + 1}`);
    mkdirSync(checkDir, { recursive: true });

    const importLines = [
      "// Generated by scripts/typecheck-generated-packages.mjs.",
      `// Imports ${info.name} entrypoints so TS-Go validates generated package module resolution.`,
    ];
    for (let index = 0; index < imports.length; index++) {
      importLines.push(`import type * as M${index} from ${JSON.stringify(imports[index])};`);
    }
    importLines.push(`export const generatedPackageImportCount = ${imports.length};`);

    const indexPath = path.join(checkDir, "index.ts");
    writeFileSync(indexPath, `${importLines.join("\n")}\n`);
    writeShardTsconfig(checkDir, [indexPath]);
    writeFileSync(path.join(checkDir, "root-files.txt"), "");

    checks.push({
      name: `${info.name} imports ${shardIndex + 1}/${importGroups.length}`,
      packageName: info.name,
      version: info.version,
      rootFiles: [],
      imports,
      checkDir,
      tsconfig: path.join(checkDir, "tsconfig.json"),
    });
  }

  for (let shardIndex = 0; shardIndex < declarationGroups.length; shardIndex++) {
    const rootFiles = declarationGroups[shardIndex];
    const checkDir = path.join(workDir, "checks", info.packageDirName, `declarations-${shardIndex + 1}`);
    mkdirSync(checkDir, { recursive: true });
    writeShardTsconfig(
      checkDir,
      rootFiles.map((file) => path.join(workDir, file))
    );
    writeFileSync(path.join(checkDir, "root-files.txt"), `${rootFiles.join("\n")}\n`);

    checks.push({
      name: `${info.name} declarations ${shardIndex + 1}/${declarationGroups.length}`,
      packageName: info.name,
      version: info.version,
      rootFiles,
      imports: [],
      checkDir,
      tsconfig: path.join(checkDir, "tsconfig.json"),
    });
  }

  totalImports += info.imports.length;
  totalRootFiles += info.rootFiles.length;
  manifestLines.push(`${info.name}@${info.version}: ${info.rootFiles.length} declarations in ${declarationGroups.length} shards, ${info.imports.length} imports in ${importGroups.length} shards`);
}

writeFileSync(path.join(workDir, "manifest.txt"), `${manifestLines.join("\n")}\n`);

console.log("Generated package declaration typecheck");
console.log(`  compiler: ${resolveTsgoBin()}`);
console.log(`  scratch:  ${workDir}`);
for (const summary of packageSummaries) {
  console.log(`  ${summary.name}@${summary.version}: ${summary.declarations} declarations, ${summary.imports} imports`);
}
console.log(`  total imports: ${totalImports}`);
console.log(`  total declaration root files: ${totalRootFiles}`);
console.log(`  total shards: ${checks.length}`);
console.log(`  declaration shard size: ${declarationShardSize}`);
console.log(`  import shard size: ${importShardSize}`);
console.log(`  shard timeout: ${elapsedText(shardTimeoutMs)}`);
console.log(`  manifest: ${path.join(workDir, "manifest.txt")}`);

const tsgoBin = resolveTsgoBin();
const requestedJobs = Number.parseInt(process.env.TSGO_TYPECHECK_JOBS ?? "", 10);
const defaultJobs = Math.max(1, Math.min(8, Math.floor((os.availableParallelism?.() ?? os.cpus().length) / 2) || 1));
const jobs = Number.isInteger(requestedJobs) && requestedJobs > 0 ? requestedJobs : defaultJobs;
const startedAt = Date.now();
const results = [];
const running = new Map();
let nextIndex = 0;
let passed = 0;
let failed = 0;

console.log(`  jobs: ${jobs}`);
console.log("");

function elapsedText(ms) {
  const totalSeconds = Math.floor(ms / 1000);
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes}m${seconds.toString().padStart(2, "0")}s`;
}

function printProgress(reason) {
  const elapsed = elapsedText(Date.now() - startedAt);
  const active = [...running.values()].map((entry) => entry.check.name).join(", ") || "-";
  console.log(`[progress] ${reason}; elapsed=${elapsed}; passed=${passed}; failed=${failed}; running=${running.size}; queued=${checks.length - nextIndex}; active=${active}`);
}

function countDiagnostics(output) {
  const diagnosticPattern = /(?:^|\n)[^\n]+\(\d+,\d+\): error (TS\d+):/g;
  const counts = new Map();
  let total = 0;
  for (const match of output.matchAll(diagnosticPattern)) {
    total++;
    counts.set(match[1], (counts.get(match[1]) ?? 0) + 1);
  }

  return {
    total,
    codes: [...counts].sort((left, right) => left[0].localeCompare(right[0])),
  };
}

async function runCheck(check, ordinal) {
  const label = `${ordinal + 1}/${checks.length} ${check.name}`;
  console.log(`[start] ${label}: declarations=${check.rootFiles.length}, imports=${check.imports.length}`);
  const start = Date.now();
  const logPath = path.join(check.checkDir, "tsgo.log");
  writeFileSync(logPath, `[start] ${label}\ncompiler: ${tsgoBin}\ntsconfig: ${check.tsconfig}\n\n`);

  return await new Promise((resolve) => {
    const child = spawn(tsgoBin, ["-p", check.tsconfig, "--pretty", "false"], {
      cwd: workDir,
      stdio: ["ignore", "pipe", "pipe"],
    });

    let output = "";
    let timedOut = false;
    const timeout = setTimeout(() => {
      timedOut = true;
      const text = `\n[timeout] exceeded ${elapsedText(shardTimeoutMs)}\n`;
      output += text;
      appendFileSync(logPath, text);
      child.kill("SIGTERM");
      setTimeout(() => child.kill("SIGKILL"), 5_000).unref();
    }, shardTimeoutMs);
    timeout.unref();

    child.stdout.on("data", (chunk) => {
      const text = chunk.toString();
      output += text;
      appendFileSync(logPath, text);
    });
    child.stderr.on("data", (chunk) => {
      const text = chunk.toString();
      output += text;
      appendFileSync(logPath, text);
    });
    child.on("error", (error) => {
      const text = String(error.stack || error.message || error);
      appendFileSync(logPath, `\n${text}\n`);
      resolve({
        check,
        label,
        status: 1,
        elapsed: Date.now() - start,
        output: text,
        logPath,
      });
    });
    child.on("close", (status) => {
      clearTimeout(timeout);
      const finalStatus = timedOut ? 124 : (status ?? 1);
      appendFileSync(logPath, `\n[exit] status=${finalStatus}; elapsed=${elapsedText(Date.now() - start)}\n`);
      resolve({
        check,
        label,
        status: finalStatus,
        elapsed: Date.now() - start,
        output,
        logPath,
      });
    });

    running.set(child.pid, { check, start });
  }).finally(() => {
    for (const [pid, entry] of running) {
      if (entry.check === check) {
        running.delete(pid);
      }
    }
  });
}

async function runQueue() {
  return await new Promise((resolve) => {
    const launchNext = () => {
      while (running.size < jobs && nextIndex < checks.length) {
        const ordinal = nextIndex;
        const check = checks[nextIndex];
        nextIndex++;
        runCheck(check, ordinal).then((result) => {
          result.diagnostics = countDiagnostics(result.output);
          results.push(result);
          if (result.status === 0) {
            passed++;
            console.log(`[pass] ${result.label}: ${elapsedText(result.elapsed)}; diagnostics=${result.diagnostics.total}; log=${path.relative(workDir, result.logPath)}`);
          } else {
            failed++;
            const codeText = result.diagnostics.codes.map(([code, count]) => `${code}:${count}`).join(", ") || "none";
            console.log(`[fail] ${result.label}: ${elapsedText(result.elapsed)}; diagnostics=${result.diagnostics.total}; codes=${codeText}; log=${path.relative(workDir, result.logPath)}`);
          }
          printProgress("shard completed");
          launchNext();
          if (results.length === checks.length) {
            resolve();
          }
        });
      }
    };

    launchNext();
  });
}

const heartbeat = setInterval(() => {
  printProgress("heartbeat");
}, 30_000);

await runQueue();
clearInterval(heartbeat);

console.log("");
console.log("Generated package declaration typecheck summary");
console.log(`  elapsed: ${elapsedText(Date.now() - startedAt)}`);
console.log(`  passed:  ${passed}`);
console.log(`  failed:  ${failed}`);

const totalDiagnostics = results.reduce((total, result) => total + result.diagnostics.total, 0);
const codeCounts = new Map();
for (const result of results) {
  for (const [code, count] of result.diagnostics.codes) {
    codeCounts.set(code, (codeCounts.get(code) ?? 0) + count);
  }
}
console.log(`  diagnostics: ${totalDiagnostics}`);
if (codeCounts.size > 0) {
  console.log(`  diagnostic codes: ${[...codeCounts].sort((left, right) => left[0].localeCompare(right[0])).map(([code, count]) => `${code}:${count}`).join(", ")}`);
}

if (failed > 0) {
  console.log("");
  console.log("Failed package checks:");
  for (const result of results.filter((entry) => entry.status !== 0)) {
    console.log(`  - ${result.check.name}: diagnostics=${result.diagnostics.total}; tsconfig=${path.relative(workDir, result.check.tsconfig)}; log=${path.relative(workDir, result.logPath)}`);
  }
}

process.exit(failed === 0 ? 0 : 1);
