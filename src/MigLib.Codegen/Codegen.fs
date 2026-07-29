namespace MigLib.Codegen

/// Public codegen API (dev-time / CLI). Depends on runtime MigLib for migrate + SQLite.
[<AutoOpen>]
module Api =

  /// Generate one F# module file per annotated relation into an output directory.
  let generate (migrationsDir: string) (outputDir: string) (namespaceName: string) : Result<CodegenResult, string> =
    Generate.generate migrationsDir outputDir namespaceName
