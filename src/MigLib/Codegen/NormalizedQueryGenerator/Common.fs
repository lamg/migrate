module internal MigLib.Codegen.NormalizedQueryGeneratorCommon

open MigLib.Schema.Types
open Fabulous.AST
open MigLib.Codegen
open MigLib.Codegen.AstExprBuilders
open MigLib.Codegen.NormalizedSchema
open MigLib.Codegen.SqlParamBindings

let getInsertColumns (table: CreateTable) : ColumnDef list =
  table.columns
  |> List.filter (fun col ->
    not (
      col.constraints
      |> List.exists (fun c ->
        match c with
        | PrimaryKey pk -> pk.isAutoincrement
        | _ -> false)
    ))

let generateInsertSql (tableName: string) (columns: ColumnDef list) : string =
  let columnNames = columns |> List.map (fun c -> c.name) |> String.concat ", "
  let paramNames = columns |> List.map (fun c -> $"@{c.name}") |> String.concat ", "
  $"INSERT INTO {tableName} ({columnNames}) VALUES ({paramNames})"

let generateInsertOrIgnoreSql (tableName: string) (columns: ColumnDef list) : string =
  let columnNames = columns |> List.map (fun c -> c.name) |> String.concat ", "
  let paramNames = columns |> List.map (fun c -> $"@{c.name}") |> String.concat ", "
  $"INSERT OR IGNORE INTO {tableName} ({columnNames}) VALUES ({paramNames})"

let generateFieldPattern (columns: ColumnDef list) : string =
  columns
  |> List.map (fun col ->
    let fieldName = TypeGenerator.toPascalCase col.name

    fieldName.ToLower().[0..0]
    + (if fieldName.Length > 1 then fieldName[1..] else ""))
  |> String.concat ", "

let generateSingleFieldPattern (columns: ColumnDef list) (targetColName: string) : string * string =
  let targetColLower = targetColName.ToLowerInvariant()

  let parts =
    columns
    |> List.map (fun col ->
      if col.name.ToLowerInvariant() = targetColLower then
        let fieldName = TypeGenerator.toPascalCase col.name

        fieldName.ToLower().[0..0]
        + (if fieldName.Length > 1 then fieldName[1..] else "")
      else
        "_")

  let pattern = parts |> String.concat ", "

  let varName =
    columns
    |> List.find (fun col -> col.name.ToLowerInvariant() = targetColLower)
    |> fun col ->
        let fieldName = TypeGenerator.toPascalCase col.name

        fieldName.ToLower().[0..0]
        + (if fieldName.Length > 1 then fieldName[1..] else "")

  pattern, varName

let getColumnVarName (column: ColumnDef) : string =
  let fieldName = TypeGenerator.toPascalCase column.name

  fieldName.ToLower().[0..0]
  + (if fieldName.Length > 1 then fieldName[1..] else "")

let generateParamBindings (columns: ColumnDef list) (cmdVarName: string) : string list =
  columns
  |> List.map (fun col -> addColumnBinding cmdVarName col (getColumnVarName col))

let getExtensionNonKeyColumns (extension: ExtensionTable) : ColumnDef list =
  let extensionFkColumns = extension.fkColumns |> Set.ofList

  extension.table.columns
  |> List.filter (fun col -> not (extensionFkColumns.Contains col.name))

let isAutoIncrementPrimaryKey (column: ColumnDef) : bool =
  column.constraints
  |> List.exists (fun constraintDef ->
    match constraintDef with
    | PrimaryKey pk -> pk.isAutoincrement
    | _ -> false)

let getPrimaryKeyColumns (table: CreateTable) : ColumnDef list =
  let tableLevelPk =
    table.constraints
    |> List.tryPick (fun c ->
      match c with
      | PrimaryKey pk when pk.columns.Length > 0 -> Some pk.columns
      | _ -> None)

  match tableLevelPk with
  | Some columns ->
    columns
    |> List.choose (fun colName -> table.columns |> List.tryFind (fun col -> col.name = colName))
  | None ->
    table.columns
    |> List.filter (fun col ->
      col.constraints
      |> List.exists (fun c ->
        match c with
        | PrimaryKey _ -> true
        | _ -> false))

let getSinglePrimaryKeyColumn (table: CreateTable) : ColumnDef option =
  match getPrimaryKeyColumns table with
  | [ singlePk ] -> Some singlePk
  | _ -> None

let getExtensionKeyPairs (baseTable: CreateTable) (extension: ExtensionTable) : (ColumnDef * string) list =
  let basePkColumns = getPrimaryKeyColumns baseTable

  if basePkColumns.Length <> extension.fkColumns.Length then
    failwith
      $"Normalized extension '{extension.table.name}' has {extension.fkColumns.Length} FK columns but base table '{baseTable.name}' has {basePkColumns.Length} PK columns."

  (basePkColumns, extension.fkColumns) ||> List.zip

let generateExtensionJoinCondition
  (baseAlias: string)
  (baseTable: CreateTable)
  (extension: ExtensionTable)
  (extensionAlias: string)
  : string =
  getExtensionKeyPairs baseTable extension
  |> List.map (fun (pkColumn, fkColumn) -> $"{baseAlias}.{pkColumn.name} = {extensionAlias}.{fkColumn}")
  |> String.concat " AND "

let generateLeftJoins (baseTable: CreateTable) (extensions: ExtensionTable list) : string =
  extensions
  |> List.mapi (fun index ext ->
    let alias = $"ext{index}"
    $"LEFT JOIN {ext.table.name} {alias} ON {generateExtensionJoinCondition baseTable.name baseTable ext alias}")
  |> String.concat "\n         "

let generateSelectColumns (baseTable: CreateTable) (extensions: ExtensionTable list) : string =
  let baseColumns =
    baseTable.columns
    |> List.map (fun col -> $"{baseTable.name}.{col.name}")
    |> String.concat ", "

  let extensionColumns =
    extensions
    |> List.mapi (fun index ext ->
      getExtensionNonKeyColumns ext
      |> List.map (fun col -> $"ext{index}.{col.name}")
      |> String.concat ", ")
    |> List.filter (fun value -> value <> "")
    |> String.concat ", "

  if extensionColumns = "" then
    baseColumns
  else
    $"{baseColumns}, {extensionColumns}"

let generateBaseFieldReads (baseTable: CreateTable) (startIndex: int) : string list =
  baseTable.columns
  |> List.mapi (fun index col ->
    let fieldName = TypeGenerator.toPascalCase col.name
    let colIndex = startIndex + index
    $"{fieldName} = {TypeGenerator.readColumnExpr col colIndex}")

let generateExtensionFieldReads (extension: ExtensionTable) (startIndex: int) : string list =
  getExtensionNonKeyColumns extension
  |> List.mapi (fun index col ->
    let fieldName = TypeGenerator.toPascalCase col.name
    let colIndex = startIndex + index
    $"{fieldName} = {TypeGenerator.readColumnExpr col colIndex}")

let private getExtensionReadWidth (extension: ExtensionTable) =
  getExtensionNonKeyColumns extension |> List.length

let generateCaseSelectionExpr (baseTable: CreateTable) (extensions: ExtensionTable list) (typeName: string) : string =
  let formatCaseExpr caseName fields =
    let allFields = fields |> String.concat ", "
    $"{typeName}.{caseName} ({allFields})"

  let baseExpr = generateBaseFieldReads baseTable 0 |> formatCaseExpr "Base"

  match extensions with
  | [] -> baseExpr
  | _ ->
    let flagNames =
      extensions
      |> List.map (fun ext -> $"has{TypeGenerator.toPascalCase ext.aspectName}")

    let nullChecks =
      extensions
      |> List.mapi (fun index ext ->
        let colIndex =
          baseTable.columns.Length
          + (extensions |> List.take index |> List.sumBy getExtensionReadWidth)

        $"let has{TypeGenerator.toPascalCase ext.aspectName} = not (reader.IsDBNull {colIndex}) in")
      |> String.concat " "

    let extensionCases =
      extensions
      |> List.mapi (fun index ext ->
        let caseName = TypeGenerator.toPascalCase ext.aspectName

        let pattern =
          extensions
          |> List.mapi (fun innerIndex _ -> if index = innerIndex then "true" else "false")
          |> String.concat ", "

        let fields =
          generateBaseFieldReads baseTable 0
          @ generateExtensionFieldReads
              ext
              (baseTable.columns.Length
               + (extensions |> List.take index |> List.sumBy getExtensionReadWidth))

        let caseExpr = formatCaseExpr ($"With{caseName}") fields
        $"| {pattern} -> {caseExpr}")

    let basePattern = extensions |> List.map (fun _ -> "false") |> String.concat ", "

    let defaultCase =
      if extensions.Length > 1 then
        [ $"| _ -> {baseExpr}" ]
      else
        []

    let matchInput =
      match flagNames with
      | [ flagName ] -> flagName
      | _ -> String.concat ", " flagNames

    String.concat
      " "
      ([ nullChecks; $"match {matchInput} with" ]
       @ extensionCases
       @ [ $"| {basePattern} -> {baseExpr}" ]
       @ defaultCase)

let getAllNormalizedColumns (normalized: NormalizedTable) : (string * ColumnDef) list =
  let baseColumns = normalized.baseTable.columns |> List.map (fun col -> "Base", col)

  let extensionColumns =
    normalized.extensions
    |> List.collect (fun ext -> ext.table.columns |> List.map (fun col -> ext.aspectName, col))

  baseColumns @ extensionColumns

let validateNormalizedQueryByAnnotation
  (normalized: NormalizedTable)
  (annotation: QueryByAnnotation)
  : Result<unit, string> =
  let allColumns = getAllNormalizedColumns normalized

  let columnNames =
    allColumns
    |> List.map (fun (_, col) -> col.name.ToLowerInvariant())
    |> Set.ofList

  annotation.columns
  |> List.tryFind (fun col -> not (columnNames.Contains(col.ToLowerInvariant())))
  |> function
    | Some invalidCol ->
      let availableCols =
        allColumns |> List.map (fun (_, col) -> col.name) |> String.concat ", "

      Error
        $"QueryBy annotation references non-existent column '{invalidCol}' in normalized table '{normalized.baseTable.name}'. Available columns: {availableCols}"
    | None -> Ok()

let validateNormalizedQueryLikeAnnotation
  (normalized: NormalizedTable)
  (annotation: QueryLikeAnnotation)
  : Result<unit, string> =
  match annotation.columns with
  | [] -> Error $"QueryLike annotation on normalized table '{normalized.baseTable.name}' requires exactly one column."
  | [ col ] ->
    let allColumns = getAllNormalizedColumns normalized

    let columnNames =
      allColumns
      |> List.map (fun (_, column) -> column.name.ToLowerInvariant())
      |> Set.ofList

    if columnNames.Contains(col.ToLowerInvariant()) then
      Ok()
    else
      let availableCols =
        allColumns |> List.map (fun (_, column) -> column.name) |> String.concat ", "

      Error
        $"QueryLike annotation references non-existent column '{col}' in normalized table '{normalized.baseTable.name}'. Available columns: {availableCols}"
  | _ ->
    let receivedCols = annotation.columns |> String.concat ", "

    Error
      $"QueryLike annotation on normalized table '{normalized.baseTable.name}' supports exactly one column. Received: {receivedCols}"

let findNormalizedColumn (normalized: NormalizedTable) (colName: string) : (string * ColumnDef) option =
  getAllNormalizedColumns normalized
  |> List.tryFind (fun (_, col) -> col.name.ToLowerInvariant() = colName.ToLowerInvariant())

let validateNormalizedQueryByOrCreateAnnotation
  (normalized: NormalizedTable)
  (annotation: QueryByOrCreateAnnotation)
  : Result<unit, string> =
  let allColumns = getAllNormalizedColumns normalized

  let allColumnNames =
    allColumns
    |> List.map (fun (_, column) -> column.name.ToLowerInvariant())
    |> Set.ofList

  annotation.columns
  |> List.tryFind (fun col -> not (allColumnNames.Contains(col.ToLowerInvariant())))
  |> function
    | Some invalidCol ->
      let availableCols =
        allColumns |> List.map (fun (_, column) -> column.name) |> String.concat ", "

      Error
        $"QueryByOrCreate annotation references non-existent column '{invalidCol}' in normalized table '{normalized.baseTable.name}'. Available columns: {availableCols}"
    | None -> Ok()

let caseHasAllQueryColumns (caseColumns: ColumnDef list) (queryColumns: string list) : bool =
  let caseColumnNames =
    caseColumns |> List.map (fun col -> col.name.ToLowerInvariant()) |> Set.ofList

  queryColumns
  |> List.forall (fun col -> caseColumnNames.Contains(col.ToLowerInvariant()))
