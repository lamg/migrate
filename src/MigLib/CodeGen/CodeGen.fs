module migrate.CodeGen.CodeGen

open System.IO
open migrate.DeclarativeMigrations
open migrate.DeclarativeMigrations.Types
open FsToolkit.ErrorHandling

/// Generate F# code for a single SQL file
let generateCodeForSqlFile (sqlFilePath: string) : Result<string, string> =
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

    // Generate module content
    let moduleContent =
      [ yield $"module {moduleName}"
        yield ""
        yield "open System"
        yield "open Microsoft.Data.Sqlite"
        yield "open FsToolkit.ErrorHandling"
        yield "open migrate.Db"
        yield ""
        // Generate record types for tables
        yield!
          sqlFile.tables
          |> List.collect (fun table -> [ TypeGenerator.generateRecordType table; "" ])

        // Generate record types for views
        yield!
          viewsWithColumns
          |> List.collect (fun (view, columns) ->
            [ TypeGenerator.generateViewRecordType view.name columns; "" ])

        // Generate query methods for tables
        yield!
          sqlFile.tables
          |> List.collect (fun table -> [ QueryGenerator.generateTableCode table; "" ])

        // Generate query methods for views (read-only)
        yield!
          viewsWithColumns
          |> List.collect (fun (view, columns) ->
            [ QueryGenerator.generateViewCode view.name columns; "" ]) ]
      |> String.concat "\n"
      |> fun s -> s.TrimEnd() // Remove trailing newlines

    FileMapper.ensureDirectory fsharpFilePath
    File.WriteAllText(fsharpFilePath, moduleContent)

    return fsharpFilePath
  }

/// Generate F# code for all SQL files in a directory
let generateCode (schemaDirectory: string) : Result<string list, string> =
  result {
    let! sqlFiles =
      try
        Directory.GetFiles(schemaDirectory, "*.sql") |> Array.toList |> Ok
      with ex ->
        Error $"Failed to find SQL files in {schemaDirectory}: {ex.Message}"

    if sqlFiles.IsEmpty then
      return! Error $"No SQL files found in {schemaDirectory}"

    let! generatedFiles = sqlFiles |> List.traverseResultM generateCodeForSqlFile

    // Generate project file
    let projectName = Path.GetFileName(Path.GetFullPath schemaDirectory)

    let projectPath =
      ProjectGenerator.writeProjectFile schemaDirectory projectName generatedFiles

    return projectPath :: generatedFiles
  }
