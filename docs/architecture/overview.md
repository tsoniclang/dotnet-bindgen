# Overview

`tsbindgen` transforms CLR assemblies into TypeScript declaration packages
through a multi-phase pipeline.

## Design goals

- complete enough CLR fidelity for Tsonic interop
- strict TypeScript output
- deterministic output
- stable imports and naming
- ergonomic callable and alias surfaces where appropriate

## What it produces

At the end of the pipeline, tsbindgen emits:

- public facade modules
- internal declaration trees
- binding metadata files
- package layouts that Tsonic can consume directly

## Pipeline summary

```text
Input (.NET DLLs)
  -> load
  -> model
  -> shape
  -> normalize
  -> plan
  -> phasegate
  -> emit
  -> output package
```
