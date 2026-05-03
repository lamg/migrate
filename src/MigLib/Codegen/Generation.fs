module internal MigLib.Codegen.Generation

open System
open System.IO
open System.Security.Cryptography
open System.Text

open MigLib.Schema.Types
open MigLib.TaskResult

type CodegenStats = { generatedFiles: string list }

let private renderBoolLiteral value = if value then "true" else "false"

let private renderStringLiteral (value: string) = sprintf "%A" value

let private renderList render items =
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
  $"{{ typeName = {renderStringLiteral enumLikeDu.typeName}; cases = {renderList renderStringLiteral enumLikeDu.cases} }}"

let private renderPrimaryKey (primaryKey: PrimaryKey) =
  $"{{ constraintName = {renderOption renderStringLiteral primaryKey.constraintName}; columns = {renderList renderStringLiteral primaryKey.columns}; isAutoincrement = {renderBoolLiteral primaryKey.isAutoincrement} }}"

let private renderForeignKey (foreignKey: ForeignKey) =
  $"{{ columns = {renderList renderStringLiteral foreignKey.columns}; refTable = {renderStringLiteral foreignKey.refTable}; refColumns = {renderList renderStringLiteral foreignKey.refColumns}; onDelete = {renderOption renderFkAction foreignKey.onDelete}; onUpdate = {renderOption renderFkAction foreignKey.onUpdate} }}"

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
  $"{{ name = {renderStringLiteral column.name}; previousName = {renderOption renderStringLiteral column.previousName}; columnType = {renderSqlType column.columnType}; constraints = {renderList renderColumnConstraint column.constraints}; enumLikeDu = {renderOption renderEnumLikeDu column.enumLikeDu}; unitOfMeasure = {renderOption renderStringLiteral column.unitOfMeasure} }}"

let private renderViewColumn (column: ViewColumn) =
  $"{{ name = {renderStringLiteral column.name}; columnType = {renderSqlType column.columnType}; enumLikeDu = {renderOption renderEnumLikeDu column.enumLikeDu}; unitOfMeasure = {renderOption renderStringLiteral column.unitOfMeasure} }}"

let private renderQueryByAnnotation (annotation: QueryByAnnotation) =
  $"{{ columns = {renderList renderStringLiteral annotation.columns} }}"

let private renderQueryLikeAnnotation (annotation: QueryLikeAnnotation) =
  $"{{ columns = {renderList renderStringLiteral annotation.columns} }}"

let private renderQueryByOrCreateAnnotation (annotation: QueryByOrCreateAnnotation) =
  $"{{ columns = {renderList renderStringLiteral annotation.columns} }}"

let private renderSelectOneAnnotation _ = "SelectOneAnnotation"

let private renderInsertOrIgnoreAnnotation _ = "InsertOrIgnoreAnnotation"

let private renderDeleteAllAnnotation _ = "DeleteAllAnnotation"

let private renderUpsertAnnotation _ = "UpsertAnnotation"

let private renderInsertInto (insert: InsertInto) =
  $"{{ table = {renderStringLiteral insert.table}; columns = {renderList renderStringLiteral insert.columns}; values = {renderList (renderList renderExpr) insert.values} }}"

let private renderCreateView (view: CreateView) =
  $"{{ name = {renderStringLiteral view.name}; previousName = {renderOption renderStringLiteral view.previousName}; sql = {renderStringLiteral view.sql}; declaredColumns = {renderList renderViewColumn view.declaredColumns}; dependencies = {renderList renderStringLiteral view.dependencies}; queryByAnnotations = {renderList renderQueryByAnnotation view.queryByAnnotations}; queryLikeAnnotations = {renderList renderQueryLikeAnnotation view.queryLikeAnnotations}; queryByOrCreateAnnotations = {renderList renderQueryByOrCreateAnnotation view.queryByOrCreateAnnotations}; selectOneAnnotations = {renderList renderSelectOneAnnotation view.selectOneAnnotations}; insertOrIgnoreAnnotations = {renderList renderInsertOrIgnoreAnnotation view.insertOrIgnoreAnnotations}; deleteAllAnnotations = {renderList renderDeleteAllAnnotation view.deleteAllAnnotations}; upsertAnnotations = {renderList renderUpsertAnnotation view.upsertAnnotations} }}"

let private renderCreateTable (table: CreateTable) =
  $"{{ name = {renderStringLiteral table.name}; previousName = {renderOption renderStringLiteral table.previousName}; dropColumns = {renderList renderStringLiteral table.dropColumns}; columns = {renderList renderColumnDef table.columns}; constraints = {renderList renderColumnConstraint table.constraints}; queryByAnnotations = {renderList renderQueryByAnnotation table.queryByAnnotations}; queryLikeAnnotations = {renderList renderQueryLikeAnnotation table.queryLikeAnnotations}; queryByOrCreateAnnotations = {renderList renderQueryByOrCreateAnnotation table.queryByOrCreateAnnotations}; selectOneAnnotations = {renderList renderSelectOneAnnotation table.selectOneAnnotations}; insertOrIgnoreAnnotations = {renderList renderInsertOrIgnoreAnnotation table.insertOrIgnoreAnnotations}; deleteAllAnnotations = {renderList renderDeleteAllAnnotation table.deleteAllAnnotations}; upsertAnnotations = {renderList renderUpsertAnnotation table.upsertAnnotations} }}"

let private renderCreateIndex (index: CreateIndex) =
  $"{{ name = {renderStringLiteral index.name}; table = {renderStringLiteral index.table}; columns = {renderList renderStringLiteral index.columns} }}"

let private renderCreateTrigger (trigger: CreateTrigger) =
  $"{{ name = {renderStringLiteral trigger.name}; sql = {renderStringLiteral trigger.sql}; dependencies = {renderList renderStringLiteral trigger.dependencies} }}"

let private renderSqlFile (schema: SqlFile) =
  $"{{ measureTypes = {renderList renderStringLiteral schema.measureTypes}; inserts = {renderList renderInsertInto schema.inserts}; views = {renderList renderCreateView schema.views}; tables = {renderList renderCreateTable schema.tables}; indexes = {renderList renderCreateIndex schema.indexes}; triggers = {renderList renderCreateTrigger schema.triggers} }}"

let private normalizeLineEndings (text: string) =
  text.Replace("\r\n", "\n").Replace("\r", "\n")

let private computeShortSchemaHash (schemaSourcePath: string) =
  try
    let normalizedSchema = File.ReadAllText schemaSourcePath |> normalizeLineEndings
    use sha256 = SHA256.Create()
    let schemaBytes = Encoding.UTF8.GetBytes normalizedSchema
    let hashBytes = sha256.ComputeHash schemaBytes

    Ok(Convert.ToHexString(hashBytes).ToLowerInvariant().Substring(0, 16))
  with ex ->
    Error $"Could not compute schema hash from source file '{schemaSourcePath}': {ex.Message}"

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

let private renderGeneratedModule moduleName dbApp schemaHash schema =
  [ $"module {moduleName}"
    ""
    "open MigLib.Schema.Types"
    "open MigLib.Codegen.Helpers"
    ""
    "[<Literal>]"
    $"let DbApp = {renderStringLiteral dbApp}"
    ""
    "[<Literal>]"
    "let DefaultDbInstance = \"main\""
    ""
    "[<Literal>]"
    $"let SchemaHash = {renderStringLiteral schemaHash}"
    ""
    "let SchemaIdentity : SchemaIdentity ="
    "  { schemaHash = SchemaHash"
    "    schemaCommit = None }"
    ""
    $"let Schema : SqlFile = {renderSqlFile schema}"
    "" ]
  |> String.concat "\n"

let generateCodeFromSchema
  (moduleName: string)
  (dbApp: string)
  (schemaSourcePath: string)
  (schema: SqlFile)
  (outputPath: string)
  : Result<CodegenStats, string> =
  result {
    do! validateModuleName moduleName

    if String.IsNullOrWhiteSpace dbApp then
      return! Error "Database app name is empty."

    let! schemaHash = computeShortSchemaHash schemaSourcePath
    let fullOutputPath = Path.GetFullPath outputPath
    let outputDirectory = Path.GetDirectoryName fullOutputPath

    if outputDirectory |> String.IsNullOrWhiteSpace |> not then
      Directory.CreateDirectory outputDirectory |> ignore

    let content = renderGeneratedModule moduleName (dbApp.Trim()) schemaHash schema
    File.WriteAllText(fullOutputPath, content)

    return { generatedFiles = [ fullOutputPath ] }
  }
