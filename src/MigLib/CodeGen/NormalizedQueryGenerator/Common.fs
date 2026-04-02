module internal Mig.CodeGen.NormalizedQueryGeneratorCommon

open Mig.DeclarativeMigrations.Types
open Fabulous.AST
open Mig.CodeGen.AstExprBuilders
open Mig.CodeGen.NormalizedSchema
open Mig.CodeGen.SqlParamBindings

let readerMethod (t: string) =
  t.Replace("int64", "Int64").Replace("string", "String").Replace("float", "Double").Replace("DateTime", "DateTime")

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
    + (if fieldName.Length > 1 then fieldName.[1..] else ""))
  |> String.concat ", "

let generateSingleFieldPattern (columns: ColumnDef list) (targetColName: string) : string * string =
  let targetColLower = targetColName.ToLowerInvariant()

  let parts =
    columns
    |> List.map (fun col ->
      if col.name.ToLowerInvariant() = targetColLower then
        let fieldName = TypeGenerator.toPascalCase col.name

        fieldName.ToLower().[0..0]
        + (if fieldName.Length > 1 then fieldName.[1..] else "")
      else
        "_")

  let pattern = parts |> String.concat ", "

  let varName =
    columns
    |> List.find (fun c -> c.name.ToLowerInvariant() = targetColLower)
    |> fun col ->
        let fieldName = TypeGenerator.toPascalCase col.name

        fieldName.ToLower().[0..0]
        + (if fieldName.Length > 1 then fieldName.[1..] else "")

  pattern, varName

let getColumnVarName (column: ColumnDef) : string =
  let fieldName = TypeGenerator.toPascalCase column.name

  fieldName.ToLower().[0..0]
  + (if fieldName.Length > 1 then fieldName.[1..] else "")

let generateParamBindings (columns: ColumnDef list) (cmdVarName: string) : string list =
  columns
  |> List.map (fun col ->
    let varName = getColumnVarName col
    let isNullable = TypeGenerator.isColumnNullable col

    if isNullable then
      $"{cmdVarName}.Parameters.AddWithValue(\"@{col.name}\", {TypeGenerator.toNullableDbValueExpr col varName}) |> ignore"
    else
      $"{cmdVarName}.Parameters.AddWithValue(\"@{col.name}\", {TypeGenerator.toDbValueExpr col varName}) |> ignore")

let getSinglePrimaryKeyColumn (table: CreateTable) : ColumnDef option =
  let tableLevelPk =
    table.constraints
    |> List.tryPick (fun c ->
      match c with
      | PrimaryKey pk when pk.columns.Length > 0 -> Some pk.columns
      | _ -> None)

  let pkColumns =
    match tableLevelPk with
    | Some cols -> table.columns |> List.filter (fun col -> List.contains col.name cols)
    | None ->
      table.columns
      |> List.filter (fun col ->
        col.constraints
        |> List.exists (fun c ->
          match c with
          | PrimaryKey _ -> true
          | _ -> false))

  match pkColumns with
  | [ singlePk ] -> Some singlePk
  | _ -> None

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
  | Some cols -> table.columns |> List.filter (fun col -> List.contains col.name cols)
  | None ->
    table.columns
    |> List.filter (fun col ->
      col.constraints
      |> List.exists (fun c ->
        match c with
        | PrimaryKey _ -> true
        | _ -> false))

let generateLeftJoins (baseTable: CreateTable) (extensions: ExtensionTable list) : string =
  let basePkColumnName =
    getSinglePrimaryKeyColumn baseTable
    |> Option.map (fun pk -> pk.name)
    |> Option.defaultValue "id"

  extensions
  |> List.mapi (fun i ext ->
    let alias = $"ext{i}"
    $"LEFT JOIN {ext.table.name} {alias} ON {baseTable.name}.{basePkColumnName} = {alias}.{ext.fkColumn}")
  |> String.concat "\n         "

let generateSelectColumns (baseTable: CreateTable) (extensions: ExtensionTable list) : string =
  let baseColumns =
    baseTable.columns
    |> List.map (fun c -> $"{baseTable.name}.{c.name}")
    |> String.concat ", "

  let extensionColumns =
    extensions
    |> List.mapi (fun i ext ->
      ext.table.columns
      |> List.filter (fun col -> col.name <> ext.fkColumn)
      |> List.map (fun c -> $"ext{i}.{c.name}")
      |> String.concat ", ")
    |> List.filter (fun s -> s <> "")
    |> String.concat ", "

  if extensionColumns = "" then
    baseColumns
  else
    $"{baseColumns}, {extensionColumns}"

let generateBaseFieldReads (baseTable: CreateTable) (startIndex: int) : string list =
  baseTable.columns
  |> List.mapi (fun i col ->
    let fieldName = TypeGenerator.toPascalCase col.name
    let colIndex = startIndex + i
    $"{fieldName} = {TypeGenerator.readColumnExpr col colIndex}")

let generateExtensionFieldReads (extension: ExtensionTable) (startIndex: int) : string list =
  extension.table.columns
  |> List.filter (fun col -> col.name <> extension.fkColumn)
  |> List.mapi (fun i col ->
    let fieldName = TypeGenerator.toPascalCase col.name
    let colIndex = startIndex + i
    $"{fieldName} = {TypeGenerator.readColumnExpr col colIndex}")

let generateCaseSelection
  (baseIndent: int)
  (baseTable: CreateTable)
  (extensions: ExtensionTable list)
  (typeName: string)
  : string =
  let indent = String.replicate baseIndent " "
  let indent4 = String.replicate (baseIndent + 4) " "

  let nullChecks =
    extensions
    |> List.mapi (fun i ext ->
      let colIndex =
        baseTable.columns.Length
        + (extensions |> List.take i |> List.sumBy (fun e -> e.table.columns.Length - 1))

      $"let has{TypeGenerator.toPascalCase ext.aspectName} = not (reader.IsDBNull {colIndex})")
    |> String.concat $"\n{indent}"

  let baseFields = generateBaseFieldReads baseTable 0 |> String.concat $",\n{indent}"

  let matchPatterns =
    extensions
    |> List.mapi (fun i ext ->
      let caseName = TypeGenerator.toPascalCase ext.aspectName

      let pattern =
        extensions
        |> List.mapi (fun j _ -> if i = j then "true" else "false")
        |> String.concat ", "

      let allFields =
        generateBaseFieldReads baseTable 0
        @ generateExtensionFieldReads
            ext
            (baseTable.columns.Length
             + (extensions |> List.take i |> List.sumBy (fun e -> e.table.columns.Length - 1)))
        |> String.concat $",\n{indent}"

      $"{indent}| {pattern} ->\n{indent4}{typeName}.With{caseName} ({allFields})")
    |> String.concat "\n"

  let basePattern = extensions |> List.map (fun _ -> "false") |> String.concat ", "

  let baseCaseMatch =
    $"{indent}| {basePattern} ->\n{indent4}{typeName}.Base ({baseFields})"

  let defaultCase =
    if extensions.Length > 1 then
      $"\n{indent}| _ ->\n{indent4}// Multiple extensions active - choosing Base case\n{indent4}{typeName}.Base ({baseFields})"
    else
      ""

  $"""{indent}{nullChecks}

{indent}match {extensions
               |> List.map (fun ext -> $"has{TypeGenerator.toPascalCase ext.aspectName}")
               |> String.concat ", "} with
{matchPatterns}
{baseCaseMatch}{defaultCase}"""

let getAllNormalizedColumns (normalized: NormalizedTable) : (string * ColumnDef) list =
  let baseColumns = normalized.baseTable.columns |> List.map (fun c -> "Base", c)

  let extensionColumns =
    normalized.extensions
    |> List.collect (fun ext -> ext.table.columns |> List.map (fun c -> ext.aspectName, c))

  baseColumns @ extensionColumns

let validateNormalizedQueryByAnnotation
  (normalized: NormalizedTable)
  (annotation: QueryByAnnotation)
  : Result<unit, string> =
  let allColumns = getAllNormalizedColumns normalized

  let columnNames =
    allColumns |> List.map (fun (_, c) -> c.name.ToLowerInvariant()) |> Set.ofList

  annotation.columns
  |> List.tryFind (fun col -> not (columnNames.Contains(col.ToLowerInvariant())))
  |> function
    | Some invalidCol ->
      let availableCols =
        allColumns |> List.map (fun (_, c) -> c.name) |> String.concat ", "

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
      allColumns |> List.map (fun (_, c) -> c.name.ToLowerInvariant()) |> Set.ofList

    if columnNames.Contains(col.ToLowerInvariant()) then
      Ok()
    else
      let availableCols =
        allColumns |> List.map (fun (_, c) -> c.name) |> String.concat ", "

      Error
        $"QueryLike annotation references non-existent column '{col}' in normalized table '{normalized.baseTable.name}'. Available columns: {availableCols}"
  | _ ->
    let receivedCols = annotation.columns |> String.concat ", "

    Error
      $"QueryLike annotation on normalized table '{normalized.baseTable.name}' supports exactly one column. Received: {receivedCols}"

let findNormalizedColumn (normalized: NormalizedTable) (colName: string) : (string * ColumnDef) option =
  getAllNormalizedColumns normalized
  |> List.tryFind (fun (_, c) -> c.name.ToLowerInvariant() = colName.ToLowerInvariant())

let validateNormalizedQueryByOrCreateAnnotation
  (normalized: NormalizedTable)
  (annotation: QueryByOrCreateAnnotation)
  : Result<unit, string> =
  let allColumns = getAllNormalizedColumns normalized

  let allColumnNames =
    allColumns |> List.map (fun (_, c) -> c.name.ToLowerInvariant()) |> Set.ofList

  annotation.columns
  |> List.tryFind (fun col -> not (allColumnNames.Contains(col.ToLowerInvariant())))
  |> function
    | Some invalidCol ->
      let availableCols =
        allColumns |> List.map (fun (_, c) -> c.name) |> String.concat ", "

      Error
        $"QueryByOrCreate annotation references non-existent column '{invalidCol}' in normalized table '{normalized.baseTable.name}'. Available columns: {availableCols}"
    | None -> Ok()

let caseHasAllQueryColumns (caseColumns: ColumnDef list) (queryColumns: string list) : bool =
  let caseColumnNames =
    caseColumns |> List.map (fun c -> c.name.ToLowerInvariant()) |> Set.ofList

  queryColumns
  |> List.forall (fun col -> caseColumnNames.Contains(col.ToLowerInvariant()))
