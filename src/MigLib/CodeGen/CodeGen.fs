module MigLib.CodeGen.CodeGen

open System
open System.IO
open FsToolkit.ErrorHandling
open MigLib.SchemaScript
open MigLib.SchemaReflection
open MigLib.DeclarativeMigrations.Types
open MigLib.CodeGen.FabulousAstHelpers
open Fantomas.Core

/// Statistics about code generation
type CodeGenStats =
  { NormalizedTables: int
    RegularTables: int
    Views: int
    GeneratedFiles: string list }

/// Generate F# code from an in-memory schema model.
/// The schema model is intentionally decoupled from input parsing so we can
/// feed it from reflection over .fsx files.
let internal generateCodeFromModel
  (moduleName: string)
  (schema: SqlFile)
  (outputFilePath: string)
  : Result<CodeGenStats, string> =
  result {
    // Extract view columns using SQLite introspection
    let! viewsWithColumns =
      schema.views
      |> List.traverseResultM (fun view ->
        result {
          let! columns = ViewIntrospection.getViewColumns schema.tables view
          return (view, columns)
        })

    // Classify tables into normalized (DU-based) and regular (option-based)
    let normalizedTables, regularTables = NormalizedSchema.classifyTables schema.tables

    // Generate query methods for regular tables (with validation)
    let! regularTableCodes =
      regularTables
      |> List.traverseResultM (fun table ->
        result {
          let! code = QueryGenerator.generateTableCode table
          return [ code; "" ]
        })
      |> Result.map List.concat

    // Generate query methods for normalized tables (with validation)
    let! normalizedTableCodes =
      normalizedTables
      |> List.traverseResultM (fun normalized ->
        result {
          let! code = NormalizedQueryGenerator.generateNormalizedTableCode normalized
          return [ code; "" ]
        })
      |> Result.map List.concat

    // Generate query methods for views (with validation)
    let! viewCodes =
      viewsWithColumns
      |> List.traverseResultM (fun (view, columns) ->
        result {
          let! code = QueryGenerator.generateViewCode view columns
          return [ code; "" ]
        })
      |> Result.map List.concat

    // Generate module content
    let moduleContent =
      [ yield $"module {moduleName}"
        yield ""
        yield "open System"
        yield "open System.Threading.Tasks"
        yield "open Microsoft.Data.Sqlite"
        yield "open FsToolkit.ErrorHandling"
        yield "open MigLib.Db"
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
        yield! normalizedTableCodes

        // Generate query methods for regular tables
        yield! regularTableCodes

        // Generate query methods for views (read-only)
        yield! viewCodes ]
      |> String.concat "\n"
      |> fun s -> s.TrimEnd() // Remove trailing newlines

    let outputDirectory = Path.GetDirectoryName outputFilePath

    if outputDirectory |> String.IsNullOrWhiteSpace |> not then
      if not (Directory.Exists outputDirectory) then
        Directory.CreateDirectory outputDirectory |> ignore

    let formattedContent =
      try
        formatCode moduleContent
      with :? ParseException ->
        moduleContent

    File.WriteAllText(outputFilePath, formattedContent)

    return
      { NormalizedTables = normalizedTables.Length
        RegularTables = regularTables.Length
        Views = viewsWithColumns.Length
        GeneratedFiles = [ outputFilePath ] }
  }

/// Generate project file alongside generated source files.
let internal writeGeneratedProjectFile (directory: string) (projectName: string) (sourceFiles: string list) : string =
  ProjectGenerator.writeProjectFile directory projectName sourceFiles

/// Generate F# code from a set of reflected CLR types.
/// This is the bridge used by the new .fsx reflection-based pipeline.
let internal generateCodeFromTypes
  (moduleName: string)
  (types: Type list)
  (outputFilePath: string)
  : Result<CodeGenStats, string> =
  result {
    let! schema = buildSchemaFromTypes types
    return! generateCodeFromModel moduleName schema outputFilePath
  }

/// Generate F# code by executing a .fsx schema script.
let generateCodeFromScript
  (moduleName: string)
  (scriptPath: string)
  (outputFilePath: string)
  : Result<CodeGenStats, string> =
  result {
    let! schema = buildSchemaFromScript scriptPath
    return! generateCodeFromModel moduleName schema outputFilePath
  }
