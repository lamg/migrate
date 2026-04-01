---
name: migrate-layered-module-splitting
description: Rule for splitting oversized source files that contain multiple responsibility layers. Use when a file mixes clearly stacked domains where lower-level helpers support higher-level workflows.
---

# Migrate Layered Module Splitting

## When to apply

- Apply this when a source file has multiple layers of responsibility.
- A good signal is that some functions form lower-level domains used by higher-level domains in one direction.
- Typical examples are files that mix primitives, metadata access, planning, and operational workflows.

## Splitting rule

- If a file contains several such layers, create a directory named after the original area or module.
- Add a `README.md` in that directory.
- Create one source file per layer.

## README requirements

- Write the `README.md` in English.
- Explain each layer's responsibility.
- Explain the dependency direction between layers.
- State that lower layers must not depend on higher layers.
- State where the public API should live.

## File layout guidance

- Put the lowest-level helpers in the lowest layer file.
- Put orchestration and public workflows in the top layer file.
- Keep the dependency flow one-way from lower layers to higher layers.
- Preserve the existing public API shape when practical, even if implementation moves internally.

## F# project updates

- Update the `.fsproj` compile order to match the layer dependency order.
- Prefer `internal` modules for lower layers unless a broader surface is required.
- Keep the top-level public module stable when possible.

## Validation

- Format changed F# files with Fantomas.
- Run the standard project tests after the split.
- Fix any broken internal references by either moving the function to the right layer or re-exporting a minimal compatibility surface when justified.
