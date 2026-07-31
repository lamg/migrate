---
name: migrate-version-sync
description: Version synchronization policy for MigLib and mig CLI packages. Use when bumping versions, releasing, or modifying features in either package to keep both versions identical.
---

# Migrate Version Sync

## Keep package versions identical

- Keep `MigLib`, `MigLib.Codegen`, and `mig`/`migtool` package versions in sync at all times.
- Update all three project files together:
  - `src/MigLib/MigLib.fsproj`
  - `src/MigLib.Codegen/MigLib.Codegen.fsproj`
  - `src/mig/mig.fsproj`

## Apply release checklist

1. Bump all three versions to the same `X.Y.Z`.
2. Verify all three project files contain the same version.
3. Update `CHANGELOG.md` for MigLib, MigLib.Codegen, and mig CLI changes.
4. Document which changes belong to which package.

## Why this policy exists

- `mig` depends on MigLib.Codegen (and thus MigLib) for code generation.
- Mismatched versions create feature availability confusion for users.
