# mig command specification

The current `mig` CLI is built around one runtime project, one `MigSchema` project, and one generated `Db.fs` module.
`mig` does not apply in-place migration history. Instead, it works with schema-bound SQLite files named from the generated schema hash.

## Project Convention

Given a runtime project directory:

- the runtime project is the single `.fsproj` in that directory
- the schema project is `MigSchema/MigSchema.fsproj`
- the schema source file is `MigSchema/MigSchema.fs`
- generated code is written to `Db.fs` in the runtime project root

Required conventions:

- the runtime project must define `<RootNamespace>`
- the schema project must define `<RootNamespace>`
- the compiled schema module must therefore be `<SchemaRootNamespace>.MigSchema`
- the generated runtime module is `<RuntimeRootNamespace>.Db`

## Generated Module Contract

`mig codegen` generates a runtime module that contains:

- `GeneratedSchema`
- generated CRUD/query helpers driven by schema attributes

Database files follow this pattern:

`<db-app>-<instance>-<schema-hash>.sqlite`

The default instance is `main`.

## Commands

### `mig codegen`

```sh
mig codegen [--dir|-d /path/to/project]
```

Behavior:

1. Discover the runtime project in the target directory.
2. Discover `MigSchema/MigSchema.fsproj`.
3. Resolve the compiled schema assembly from the schema project build output.
4. Load `<SchemaRootNamespace>.MigSchema` and read its `Schema` value.
5. Generate `Db.fs` into the runtime project root.

Output includes:

- generated module name
- output path
- generated file list

### `mig init`

```sh
mig init [--dir|-d /path/to/project] [--instance|-i name]
```

Behavior:

1. Discover the runtime project and generated runtime module from the compiled runtime assembly.
2. Resolve the target database path from `GeneratedSchema.dbApp`, `instance`, and `GeneratedSchema.schemaHash`.
3. If the target database already exists, return success without recreating it.
4. Otherwise create the schema-matched database and apply seed inserts.

Output includes:

- target database path
- seeded row count

### `mig plan`

```sh
mig plan [--dir|-d /path/to/project] [--instance|-i name]
```

Behavior:

1. Discover the current target database path.
2. Infer the source database as exactly one other matching SQLite file for the same app/instance prefix.
3. Compare the source schema with the generated target schema.
4. Report supported and unsupported differences.

Output includes:

- source database path or `none`
- target database path
- `Can migrate: yes|no`
- supported differences
- unsupported differences

Exit code:

- `0` when the migration can run
- `1` when blocking differences were found

### `mig migrate`

```sh
mig migrate [--dir|-d /path/to/project] [--instance|-i name]
```

Behavior:

1. Build the same migration plan used by `mig plan`.
2. Refuse to run when unsupported differences are present.
3. Create the new target database.
4. Copy compatible data from the inferred source database when one exists.
5. Mark the source database readonly and move it into `archive/` next to the database directory.
6. Return a ready-to-use target database path.

Output includes:

- target database path
- copied table count
- copied row count
- archived old database path when a source database existed

### `mig status`

```sh
mig status [--dir|-d /path/to/project] [--instance|-i name]
```

Behavior:

1. Resolve the current target database path when it exists.
2. List archived databases in `archive/`.
3. Report whether a source database is still present and therefore migration is needed.

Output includes:

- current database path or `none`
- archived database paths
- `Needs migration: yes|no`

### `mig reset`

```sh
mig reset [--dir|-d /path/to/project] [--instance|-i name]
```

Behavior:

1. Resolve the current target database path.
2. Delete it when it exists.
3. Restore the latest archived database back into the main database directory.
4. Remove the `_mig_readonly` marker from the restored database.

Output includes:

- removed current database path or `none`
- restored database path or `none`

## Command Relationships

- `codegen` must run after the schema project is built.
- `init`, `plan`, `migrate`, `status`, and `reset` require the runtime project to be built after code generation so the compiled runtime assembly contains the generated module.
- `migrate` archives the previous source database immediately after a successful copy.
- `reset` is the workflow-level undo for the current target database plus the latest archived source.
