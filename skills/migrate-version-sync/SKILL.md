---
name: migrate-version-sync
description: Version synchronization policy for MigLib and mig CLI packages. Use when bumping versions, releasing, or modifying features in either package to keep both versions identical.
---

# Migrate Version Sync

## Keep package versions identical

- Keep `MigLib` and `mig` package versions in sync at all times.
- Update both project files together:
- `src/MigLib/MigLib.fsproj`
- `src/mig/mig.fsproj`

## Apply release checklist

1. Bump both versions to the same `X.Y.Z`.
2. Verify both project files contain the same version.
3. Update `CHANGELOG.md` for both MigLib and mig CLI changes.
4. Document which changes belong to MigLib and which belong to mig CLI.

## Why this policy exists

- `mig` depends on MigLib behavior used for code generation.
- Mismatched versions create feature availability confusion for users.
