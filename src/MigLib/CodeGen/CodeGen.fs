module Mig.CodeGen.CodeGen

open System
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Text
open Mig.SchemaReflection
open Mig.DeclarativeMigrations.Types
open Mig.CodeGen.FabulousAstHelpers
open MigLib.Db
open MigLib.Util
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
  && segment |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_' || c = '\'')

let private validateModuleName (moduleName: string) =
  let segments = moduleName.Split '.'

  if segments.Length = 0 || segments |> Array.exists (isValidModuleSegment >> not) then
    Error $"Module name '{moduleName}' is not a valid F# module identifier."
  else
    Ok()

let private normalizeLineEndings (text: string) =
  text.Replace("\r\n", "\n").Replace("\r", "\n")

let private computeShortSchemaHash (schemaPath: string) : Result<string, string> =
  try
    let normalizedSchema = File.ReadAllText schemaPath |> normalizeLineEndings
    use sha256 = SHA256.Create()
    let schemaBytes = Encoding.UTF8.GetBytes normalizedSchema
    let hashBytes = sha256.ComputeHash schemaBytes
    Ok(Convert.ToHexString(hashBytes).ToLowerInvariant().Substring(0, 16))
  with ex ->
    Error $"Could not compute schema hash from source file '{schemaPath}': {ex.Message}"

type private SchemaGenerationMetadata =
  { schemaHash: string
    dbApp: string }

let private deriveSchemaGenerationMetadata
  (dbApp: string)
  (schemaPath: string)
  : Result<SchemaGenerationMetadata, string> =
  result {
    if String.IsNullOrWhiteSpace dbApp then
      return! Error "Database app name is empty."

    let! schemaHash = computeShortSchemaHash schemaPath

    return
      { schemaHash = schemaHash
        dbApp = dbApp.Trim() }
  }

let private deriveDatabaseFileName
  (dbApp: string)
  (instance: string option)
  (schemaPath: string)
  : Result<string, string> =
  result {
    let! metadata = deriveSchemaGenerationMetadata dbApp schemaPath
    return! buildSchemaBoundDbFileName metadata.dbApp instance metadata.schemaHash
  }

let internal deriveDatabaseFileNameFromSourcePath
  (dbApp: string)
  (instance: string option)
  (schemaPath: string)
  : Result<string, string> =
  deriveDatabaseFileName dbApp instance schemaPath

let private renderBoolLiteral (value: bool) = if value then "true" else "false"

let private renderStringLiteral (value: string) = sprintf "%A" value

let private renderList render (items: 'a list) =
  items
  |> List.map render
  |> String.concat "; "
  |> fun rendered -> $"[ {rendered} ]"

let private renderOption render value =
  match value with
  | Some item -> $"Some ({render item})"
  | None -> "None"

let private renderSqlType =
  function
  | SqlInteger -> "SqlType.SqlInteger"
  | SqlText -> "SqlType.SqlText"
  | SqlReal -> "SqlType.SqlReal"
  | SqlTimestamp -> "SqlType.SqlTimestamp"
  | SqlString -> "SqlType.SqlString"

let private renderExpr =
  function
  | String value -> $"Expr.String {renderStringLiteral value}"
  | Integer value -> $"Expr.Integer {value}"
  | Real value ->
    let renderedValue = value.ToString("R", Globalization.CultureInfo.InvariantCulture)
    $"Expr.Real {renderedValue}"
  | Value value -> $"Expr.Value {renderStringLiteral value}"

let private renderFkAction =
  function
  | Cascade -> "FkAction.Cascade"
  | Restrict -> "FkAction.Restrict"
  | NoAction -> "FkAction.NoAction"
  | SetNull -> "FkAction.SetNull"
  | SetDefault -> "FkAction.SetDefault"

let private renderEnumLikeDu (enumLikeDu: EnumLikeDu) =
  let renderedCases = renderList renderStringLiteral enumLikeDu.cases
  $"{{ typeName = {renderStringLiteral enumLikeDu.typeName}; cases = {renderedCases} }}"

let private renderPrimaryKey (primaryKey: PrimaryKey) =
  let renderedConstraintName =
    renderOption renderStringLiteral primaryKey.constraintName

  let renderedColumns = renderList renderStringLiteral primaryKey.columns
  let renderedAutoincrement = renderBoolLiteral primaryKey.isAutoincrement

  $"{{ constraintName = {renderedConstraintName}; columns = {renderedColumns}; isAutoincrement = {renderedAutoincrement} }}"

let private renderForeignKey (foreignKey: ForeignKey) =
  let renderedColumns = renderList renderStringLiteral foreignKey.columns
  let renderedRefColumns = renderList renderStringLiteral foreignKey.refColumns
  let renderedOnDelete = renderOption renderFkAction foreignKey.onDelete
  let renderedOnUpdate = renderOption renderFkAction foreignKey.onUpdate

  $"{{ columns = {renderedColumns}; refTable = {renderStringLiteral foreignKey.refTable}; refColumns = {renderedRefColumns}; onDelete = {renderedOnDelete}; onUpdate = {renderedOnUpdate} }}"

let private renderColumnConstraint =
  function
  | PrimaryKey primaryKey -> $"ColumnConstraint.PrimaryKey {renderPrimaryKey primaryKey}"
  | Autoincrement -> "ColumnConstraint.Autoincrement"
  | NotNull -> "ColumnConstraint.NotNull"
  | Unique columns -> $"ColumnConstraint.Unique {renderList renderStringLiteral columns}"
  | Default expr -> $"ColumnConstraint.Default ({renderExpr expr})"
  | Check clauses -> $"ColumnConstraint.Check {renderList renderStringLiteral clauses}"
  | ForeignKey foreignKey -> $"ColumnConstraint.ForeignKey {renderForeignKey foreignKey}"

let private renderColumnDef (column: ColumnDef) =
  let renderedPreviousName = renderOption renderStringLiteral column.previousName
  let renderedConstraints = renderList renderColumnConstraint column.constraints
  let renderedEnumLikeDu = renderOption renderEnumLikeDu column.enumLikeDu
  let renderedUnitOfMeasure = renderOption renderStringLiteral column.unitOfMeasure

  $"{{ name = {renderStringLiteral column.name}; previousName = {renderedPreviousName}; columnType = {renderSqlType column.columnType}; constraints = {renderedConstraints}; enumLikeDu = {renderedEnumLikeDu}; unitOfMeasure = {renderedUnitOfMeasure} }}"

let private renderViewColumn (column: ViewColumn) =
  let renderedEnumLikeDu = renderOption renderEnumLikeDu column.enumLikeDu
  let renderedUnitOfMeasure = renderOption renderStringLiteral column.unitOfMeasure

  $"{{ name = {renderStringLiteral column.name}; columnType = {renderSqlType column.columnType}; enumLikeDu = {renderedEnumLikeDu}; unitOfMeasure = {renderedUnitOfMeasure} }}"

let private renderQueryByAnnotation (annotation: QueryByAnnotation) =
  $"{{ columns = {renderList renderStringLiteral annotation.columns} }}"

let private renderQueryLikeAnnotation (annotation: QueryLikeAnnotation) =
  $"{{ columns = {renderList renderStringLiteral annotation.columns} }}"

let private renderQueryByOrCreateAnnotation (annotation: QueryByOrCreateAnnotation) =
  $"{{ columns = {renderList renderStringLiteral annotation.columns} }}"

let private renderInsertInto (insert: InsertInto) =
  let renderedColumns = renderList renderStringLiteral insert.columns
  let renderedValues = renderList (renderList renderExpr) insert.values
  $"{{ table = {renderStringLiteral insert.table}; columns = {renderedColumns}; values = {renderedValues} }}"

let private renderCreateView (view: CreateView) =
  let renderedPreviousName = renderOption renderStringLiteral view.previousName
  let renderedSqlTokens = renderList renderStringLiteral (List.ofSeq view.sqlTokens)
  let renderedDeclaredColumns = renderList renderViewColumn view.declaredColumns
  let renderedDependencies = renderList renderStringLiteral view.dependencies

  let renderedQueryByAnnotations =
    renderList renderQueryByAnnotation view.queryByAnnotations

  let renderedQueryLikeAnnotations =
    renderList renderQueryLikeAnnotation view.queryLikeAnnotations

  let renderedQueryByOrCreateAnnotations =
    renderList renderQueryByOrCreateAnnotation view.queryByOrCreateAnnotations

  let renderedInsertOrIgnoreAnnotations =
    renderList (fun _ -> "InsertOrIgnoreAnnotation") view.insertOrIgnoreAnnotations

  let renderedDeleteAllAnnotations =
    renderList (fun _ -> "DeleteAllAnnotation") view.deleteAllAnnotations

  let renderedUpsertAnnotations =
    renderList (fun _ -> "UpsertAnnotation") view.upsertAnnotations

  $"{{ name = {renderStringLiteral view.name}; previousName = {renderedPreviousName}; sqlTokens = {renderedSqlTokens}; declaredColumns = {renderedDeclaredColumns}; dependencies = {renderedDependencies}; queryByAnnotations = {renderedQueryByAnnotations}; queryLikeAnnotations = {renderedQueryLikeAnnotations}; queryByOrCreateAnnotations = {renderedQueryByOrCreateAnnotations}; insertOrIgnoreAnnotations = {renderedInsertOrIgnoreAnnotations}; deleteAllAnnotations = {renderedDeleteAllAnnotations}; upsertAnnotations = {renderedUpsertAnnotations} }}"

let private renderCreateTable (table: CreateTable) =
  let renderedPreviousName = renderOption renderStringLiteral table.previousName
  let renderedDropColumns = renderList renderStringLiteral table.dropColumns
  let renderedColumns = renderList renderColumnDef table.columns
  let renderedConstraints = renderList renderColumnConstraint table.constraints

  let renderedQueryByAnnotations =
    renderList renderQueryByAnnotation table.queryByAnnotations

  let renderedQueryLikeAnnotations =
    renderList renderQueryLikeAnnotation table.queryLikeAnnotations

  let renderedQueryByOrCreateAnnotations =
    renderList renderQueryByOrCreateAnnotation table.queryByOrCreateAnnotations

  let renderedInsertOrIgnoreAnnotations =
    renderList (fun _ -> "InsertOrIgnoreAnnotation") table.insertOrIgnoreAnnotations

  let renderedDeleteAllAnnotations =
    renderList (fun _ -> "DeleteAllAnnotation") table.deleteAllAnnotations

  let renderedUpsertAnnotations =
    renderList (fun _ -> "UpsertAnnotation") table.upsertAnnotations

  $"{{ name = {renderStringLiteral table.name}; previousName = {renderedPreviousName}; dropColumns = {renderedDropColumns}; columns = {renderedColumns}; constraints = {renderedConstraints}; queryByAnnotations = {renderedQueryByAnnotations}; queryLikeAnnotations = {renderedQueryLikeAnnotations}; queryByOrCreateAnnotations = {renderedQueryByOrCreateAnnotations}; insertOrIgnoreAnnotations = {renderedInsertOrIgnoreAnnotations}; deleteAllAnnotations = {renderedDeleteAllAnnotations}; upsertAnnotations = {renderedUpsertAnnotations} }}"

let private renderCreateIndex (index: CreateIndex) =
  $"{{ name = {renderStringLiteral index.name}; table = {renderStringLiteral index.table}; columns = {renderList renderStringLiteral index.columns} }}"

let private renderCreateTrigger (trigger: CreateTrigger) =
  let renderedSqlTokens =
    renderList renderStringLiteral (List.ofSeq trigger.sqlTokens)

  let renderedDependencies = renderList renderStringLiteral trigger.dependencies
  $"{{ name = {renderStringLiteral trigger.name}; sqlTokens = {renderedSqlTokens}; dependencies = {renderedDependencies} }}"

let private renderSqlFile (schema: SqlFile) =
  let renderedMeasureTypes = renderList renderStringLiteral schema.measureTypes
  let renderedInserts = renderList renderInsertInto schema.inserts
  let renderedViews = renderList renderCreateView schema.views
  let renderedTables = renderList renderCreateTable schema.tables
  let renderedIndexes = renderList renderCreateIndex schema.indexes
  let renderedTriggers = renderList renderCreateTrigger schema.triggers

  $"{{ measureTypes = {renderedMeasureTypes}; inserts = {renderedInserts}; views = {renderedViews}; tables = {renderedTables}; indexes = {renderedIndexes}; triggers = {renderedTriggers} }}"

/// Generate F# code from an in-memory schema model.
/// The schema model is intentionally decoupled from input parsing so callers
/// can feed it from compiled schema reflection or their own schema builders.
let private generateCode
  (moduleName: string)
  (dbApp: string option)
  (schema: SqlFile)
  (outputFilePath: string)
  (schemaHash: string option)
  : Result<CodeGenStats, string> =
  result {
    do! validateModuleName moduleName

    // Extract view columns using SQLite introspection
    let! viewsWithColumns =
      schema.views
      |> traverseResultM (fun view ->
        result {
          let! columns = ViewIntrospection.getViewColumns schema.tables view
          return view, columns
        })

    // Classify tables into normalized (DU-based) and regular (option-based)
    let normalizedTables, regularTables = NormalizedSchema.classifyTables schema.tables

    // Generate query methods for regular tables (with validation)
    let! regularTableCodes =
      regularTables
      |> traverseResultM (fun table ->
        result {
          let! code = QueryGenerator.generateTableCode table
          return [ code; "" ]
        })
      |> Result.map List.concat

    // Generate query methods for normalized tables (with validation)
    let! normalizedTableCodes =
      normalizedTables
      |> traverseResultM (fun normalized ->
        result {
          let! code = NormalizedQueryGenerator.generateNormalizedTableCode normalized
          return [ code; "" ]
        })
      |> Result.map List.concat

    // Generate query methods for views (with validation)
    let! viewCodes =
      viewsWithColumns
      |> traverseResultM (fun (view, columns) ->
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
        yield "open Mig.DeclarativeMigrations.Types"
        yield "open Mig.HotMigration"
        yield "open MigLib.Db"
        yield "open MigLib.Util"
        yield ""
        match dbApp with
        | Some appName ->
          yield "[<Literal>]"
          yield $"let DbApp = \"{appName}\""
          yield ""
          yield "[<Literal>]"
          yield $"let DefaultDbInstance = \"{DefaultDatabaseInstance}\""
          yield ""
        | None -> ()
        match schemaHash with
        | Some value ->
          yield "[<Literal>]"
          yield $"let SchemaHash = {renderStringLiteral value}"
          yield ""
          yield "let SchemaIdentity : SchemaIdentity ="
          yield "  { schemaHash = SchemaHash"
          yield "    schemaCommit = None }"
          yield ""
          match dbApp with
          | Some _ ->
            yield "let DbFileForInstance (instance: string option) ="
            yield "  buildSchemaBoundDbFileName DbApp instance SchemaHash"
            yield "  |> ResultEx.orFail invalidOp"
          | None -> ()
          yield ""
        | None -> ()
        yield $"let Schema : SqlFile = {renderSqlFile schema}"
        yield ""
        yield!
          schema.measureTypes
          |> List.collect (fun measureType -> [ TypeGenerator.generateMeasureType measureType; "" ])
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

let internal generateCodeFromModel
  (moduleName: string)
  (schema: SqlFile)
  (outputFilePath: string)
  : Result<CodeGenStats, string> =
  generateCode moduleName None schema outputFilePath None

/// Generate F# code from a set of reflected CLR types.
/// This is the bridge used by compiled-schema code generation.
let internal generateCodeFromTypes
  (moduleName: string)
  (types: Type list)
  (outputFilePath: string)
  : Result<CodeGenStats, string> =
  result {
    let! schema = buildSchemaFromTypes types
    return! generateCode moduleName None schema outputFilePath None
  }

let internal generateCodeFromTypesWithDbFile
  (moduleName: string)
  (dbApp: string)
  (schemaPath: string)
  (types: Type list)
  (outputFilePath: string)
  : Result<CodeGenStats, string> =
  result {
    let! schema = buildSchemaFromTypes types
    let! metadata = deriveSchemaGenerationMetadata dbApp schemaPath

    return! generateCode moduleName (Some metadata.dbApp) schema outputFilePath (Some metadata.schemaHash)
  }

let internal generateCodeFromAssemblyModuleWithDbFile
  (generatedModuleName: string)
  (dbApp: string)
  (schemaPath: string)
  (assembly: Assembly)
  (schemaModuleName: string)
  (outputFilePath: string)
  : Result<CodeGenStats, string> =
  result {
    let! schema = buildSchemaFromAssemblyModule assembly schemaModuleName
    let! metadata = deriveSchemaGenerationMetadata dbApp schemaPath

    return! generateCode generatedModuleName (Some metadata.dbApp) schema outputFilePath (Some metadata.schemaHash)
  }
