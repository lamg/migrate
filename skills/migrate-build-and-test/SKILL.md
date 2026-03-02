---
name: migrate-build-and-test
description: Build, test, and install workflow for the migrate project. Use when validating changes, building release artifacts, or installing the `mig` tool so commands follow the canonical `build.fsx` flow.
---

# Migrate Build And Test

## Run tests

- Run `cd src && dotnet test` after changes.
- Assume test stdout is captured in .NET 10; do not rely on print-debug output from tests.

## Use canonical build targets

- Use `build.fsx` as the canonical build/install entrypoint.
- Avoid ad-hoc install flows for `mig` when a build target exists.

## Common commands

- Build only (Release): `dotnet fsi build.fsx -- --target Build`
- Build, pack, and install global `mig`: `dotnet fsi build.fsx -- --target InstallGlobal`
