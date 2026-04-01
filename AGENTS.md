# Instructions for migrate Project

This file is intentionally short. Detailed project guidance is split into local skills under `skills/`.

## Always Apply

- Follow the relevant project skill(s) listed below.
- Format changed F# files with Fantomas.
- Run `cd src && dotnet test` for change validation.
- Use `build.fsx` targets instead of ad-hoc `mig` install/build workflows.

## Skills

### Available skills

- migrate-fsharp-conventions: F# style and formatting rules for `.fs`/`.fsi` edits. (file: `skills/migrate-fsharp-conventions/SKILL.md`)
- migrate-build-and-test: Canonical test/build/install commands. (file: `skills/migrate-build-and-test/SKILL.md`)
- migrate-codegen-cpm: `mig codegen` `.fsproj` and CPM requirements. (file: `skills/migrate-codegen-cpm/SKILL.md`)
- migrate-version-sync: MigLib and mig version synchronization and release checklist. (file: `skills/migrate-version-sync/SKILL.md`)
- migrate-docs-workflow: `specs/` and `PROGRESS.md` documentation workflow. (file: `skills/migrate-docs-workflow/SKILL.md`)
- migrate-layered-module-splitting: Split oversized multi-layer files into a directory with one file per layer plus a README. (file: `skills/migrate-layered-module-splitting/SKILL.md`)
