module migrate.CodeGen.CodeGen

open System.IO
open migrate.DeclarativeMigrations
open migrate.DeclarativeMigrations.Types
open FsToolkit.ErrorHandling

/// Statistics about code generation
type CodeGenStats =
  { NormalizedTables: int
    RegularTables: int
    Views: int
    GeneratedFiles: string list }

/// Generate F# code for a single SQL file and return statistics
let generateCodeForSqlFile (sqlFilePath: string) : Result<string * int * int * int, string> =
  result {
    let! sqlContent =
      try
        Ok(File.ReadAllText sqlFilePath)
      with ex ->
        Error $"Failed to read SQL file {sqlFilePath}: {ex.Message}"

    let! sqlFile = FParsecSqlParser.parseSqlFile (sqlFilePath, sqlContent)

    let moduleName = FileMapper.sqlFileToModuleName sqlFilePath
    let fsharpFilePath = FileMapper.sqlFileToFSharpFile sqlFilePath

    // Extract view columns using SQLite introspection
    let! viewsWithColumns =
      sqlFile.views
      |> List.traverseResultM (fun view ->
        result {
          let! columns = ViewIntrospection.getViewColumns sqlFile.tables view
          return (view, columns)
        })

    // Classify tables into normalized (DU-based) and regular (option-based)
    let normalizedTables, regularTables = NormalizedSchema.classifyTables sqlFile.tables

    // Generate module content
    let moduleContent =
      [ yield $"module {moduleName}"
        yield ""
        yield "open System"
        yield "open Microsoft.Data.Sqlite"
        yield "open FsToolkit.ErrorHandling"
        yield "open migrate.Db"
        yield ""
        // Generate discriminated union types for normalized tables
        yield!
          normalizedTables
          |> List.collect (fun normalized -> [ NormalizedTypeGenerator.generateTypes normalized; "" ])

        // Generate record types for regular tables
        yield!
          regularTables
          |> List.collect (fun table -> [ TypeGenerator.generateRecordType table; "" ])

        // Generate record types for views
        yield!
          viewsWithColumns
          |> List.collect (fun (view, columns) -> [ TypeGenerator.generateViewRecordType view.name columns; "" ])

        // Generate query methods for normalized tables (with DU pattern matching)
        yield!
          normalizedTables
          |> List.collect (fun normalized -> [ NormalizedQueryGenerator.generateNormalizedTableCode normalized; "" ])

        // Generate query methods for regular tables
        yield!
          regularTables
          |> List.collect (fun table -> [ QueryGenerator.generateTableCode table; "" ])

        // Generate query methods for views (read-only)
        yield!
          viewsWithColumns
          |> List.collect (fun (view, columns) -> [ QueryGenerator.generateViewCode view.name columns; "" ]) ]
      |> String.concat "\n"
      |> fun s -> s.TrimEnd() // Remove trailing newlines

    FileMapper.ensureDirectory fsharpFilePath
    File.WriteAllText(fsharpFilePath, moduleContent)

    // Return file path and counts
    return (fsharpFilePath, normalizedTables.Length, regularTables.Length, viewsWithColumns.Length)
  }

/// Generate F# code for all SQL files in a directory
let generateCode (schemaDirectory: string) : Result<CodeGenStats, string> =
  result {
    let! sqlFiles =
      try
        Directory.GetFiles(schemaDirectory, "*.sql") |> Array.toList |> Ok
      with ex ->
        Error $"Failed to find SQL files in {schemaDirectory}: {ex.Message}"

    if sqlFiles.IsEmpty then
      return! Error $"No SQL files found in {schemaDirectory}"

    let! results = sqlFiles |> List.traverseResultM generateCodeForSqlFile

    // Aggregate statistics
    let generatedFiles = results |> List.map (fun (file, _, _, _) -> file)
    let totalNormalized = results |> List.sumBy (fun (_, norm, _, _) -> norm)
    let totalRegular = results |> List.sumBy (fun (_, _, reg, _) -> reg)
    let totalViews = results |> List.sumBy (fun (_, _, _, views) -> views)

    // Generate project file
    let projectName = Path.GetFileName(Path.GetFullPath schemaDirectory)

    let projectPath =
      ProjectGenerator.writeProjectFile schemaDirectory projectName generatedFiles

    return
      { NormalizedTables = totalNormalized
        RegularTables = totalRegular
        Views = totalViews
        GeneratedFiles = projectPath :: generatedFiles }
  }
