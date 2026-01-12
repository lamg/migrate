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
      [ $"module {moduleName}"
        ""
        "open System"
        "open Microsoft.Data.Sqlite"
        "open FsToolkit.ErrorHandling"
        "open migrate.Db"
        ""
        // Generate record types for tables
        yield!
          sqlFile.tables
          |> List.map (fun table ->
            let typeName = QueryGenerator.capitalize table.name

            let fields =
              table.columns
              |> List.map (fun col ->
                let fieldName = QueryGenerator.capitalize col.name
                let isNullable = TypeGenerator.isColumnNullable col
                let fsharpType = TypeGenerator.mapSqlType col.columnType isNullable
                $"  {fieldName}: {fsharpType}")
              |> String.concat "\n"

            $"type {typeName} = {{\n{fields}\n}}")

        // Generate record types for views
        yield!
          viewsWithColumns
          |> List.map (fun (view, columns) -> TypeGenerator.generateViewRecordType view.name columns)

        ""
        // Generate query methods for tables
        yield! sqlFile.tables |> List.map QueryGenerator.generateTableCode

        // Generate query methods for views (read-only)
        yield!
          viewsWithColumns
          |> List.map (fun (view, columns) -> QueryGenerator.generateViewCode view.name columns) ]
      |> String.concat "\n"

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
