module Mig.CodeGen.CodeGen

open System
open System.IO
open FsToolkit.ErrorHandling
open Mig.SchemaScript
open Mig.SchemaReflection
open Mig.DeclarativeMigrations.Types
open Mig.CodeGen.FabulousAstHelpers
open Fantomas.Core

/// Statistics about code generation
type CodeGenStats =
  { NormalizedTables: int
    RegularTables: int
    Views: int
    GeneratedFiles: string list }

let private isValidModuleSegment (segment: string) =
  not (String.IsNullOrWhiteSpace segment)
  && (Char.IsLetter segment[0] || segment[0] = '_')
  && (segment |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_' || c = '\''))

let private validateModuleName (moduleName: string) =
  let segments = moduleName.Split('.')

  if segments.Length = 0 || segments |> Array.exists (isValidModuleSegment >> not) then
    Error $"Module name '{moduleName}' is not a valid F# module identifier."
  else
    Ok()

/// Generate F# code from an in-memory schema model.
/// The schema model is intentionally decoupled from input parsing so we can
/// feed it from reflection over .fsx files.
let internal generateCodeFromModel
  (moduleName: string)
  (schema: SqlFile)
  (outputFilePath: string)
  : Result<CodeGenStats, string> =
  result {
    do! validateModuleName moduleName

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
    let enumLikeDus =
      (regularTables
       |> List.collect (fun table -> TypeGenerator.collectEnumLikeDusFromColumns table.columns))
      @ (normalizedTables
         |> List.collect (fun normalized ->
           TypeGenerator.collectEnumLikeDusFromColumns normalized.baseTable.columns
           @ (normalized.extensions
              |> List.collect (fun extensionTable ->
                TypeGenerator.collectEnumLikeDusFromColumns extensionTable.table.columns))))
      @ (viewsWithColumns
         |> List.collect (fun (_, columns) -> TypeGenerator.collectEnumLikeDusFromViewColumns columns))
      |> List.distinctBy (fun enumLikeDu -> enumLikeDu.typeName, enumLikeDu.cases)

    let moduleContent =
      [ yield $"module {moduleName}"
        yield ""
        yield "open System"
        yield "open System.Threading.Tasks"
        yield "open Microsoft.Data.Sqlite"
        yield "open FsToolkit.ErrorHandling"
        yield "open MigLib.Db"
        yield ""
        yield!
          enumLikeDus
          |> List.collect (fun enumLikeDu -> [ TypeGenerator.generateEnumType enumLikeDu; "" ])
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

    let! formattedContent =
      try
        Ok(formatCode moduleContent)
      with :? ParseException as ex ->
        Error $"Generated F# code could not be parsed for module '{moduleName}': {ex.Message}"

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
