# Library Mode

Library mode is for generating bindings for custom CLR assemblies rather than
the built-in first-party binding repos.

## Use cases

- internal company assemblies
- local experimentation
- custom libraries that should participate in Tsonic interop

## Why it exists

Library mode lets your generated package import shared CLR types from existing
packages instead of re-owning the entire BCL surface.

That is how large generated ecosystems stay composable.

## Typical shape

Generate core framework/BCL packages first, then generate your own assembly
package against those existing libraries.
