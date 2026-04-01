# Program Layers

This directory contains the internal layers behind the `mig` command-line entrypoint.

`../Program.fs` remains the thin entrypoint and dispatch facade. The implementation behind argument parsing, path resolution, and command execution lives here in dependency order.

## Layers

### Args

Responsibilities:

- define the CLI argument types and subcommands
- keep Argu-specific metadata in one place

This is the lowest layer for the CLI.

### Common

Responsibilities:

- define small shared record types used across command resolution and execution
- centralize common helpers such as version rendering, exception formatting, and final command result handling

This layer depends on `Args` but not on command execution.

### Resolution

Responsibilities:

- resolve working directories, inferred database paths, and schema-bound target database locations
- discover compiled assemblies and generated modules for runtime and codegen commands
- produce normalized command inputs consumed by the command handlers

This layer builds on `Common` and keeps inference and discovery logic out of the top command handlers.

### BuildCommands

Responsibilities:

- implement the build-side commands: `codegen` and `init`
- orchestrate code generation and database initialization using already-resolved inputs

This layer depends on the lower resolution and common layers.

### MigrationCommands

Responsibilities:

- implement migrate, offline, plan, drain, cutover, archive-old, reset, and status
- own CLI-specific recovery messaging and operational output

This is the top operational layer for the CLI and depends on all lower layers it needs.

## Dependency Direction

The intended direction is:

`Args -> Common -> Resolution -> BuildCommands/MigrationCommands -> ../Program.fs`

Lower layers should not depend on higher ones. When adding new CLI code, keep it in the lowest layer that can own the responsibility cleanly.
