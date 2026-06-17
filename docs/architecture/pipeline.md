# Pipeline

## Phases

### Load

Read CLR assemblies and reflect public type information.

### Model

Build type indices and assemble the symbol graph.

### Shape

Transform CLR-oriented symbols into a TypeScript-friendly intermediate form.

### Normalize

Reserve and stabilize TypeScript identifiers.

### Plan

Plan imports, exports, aliases, and output package structure.

### Phasegate

Validate invariants before writing files.

### Emit

Write declaration files, facades, and binding metadata.

## Why these phases exist

The pipeline is intentionally split so dotnet-bindgen can:

- preserve CLR identity
- still emit readable TypeScript
- catch naming and ownership conflicts before file output
- keep large binding waves deterministic
