module internal MigLib.Codegen.NormalizedSchema

open MigLib.Schema.Types

type ExtensionTable =
  { table: CreateTable
    aspectName: string
    fkColumns: string list }

type NormalizedTable =
  { baseTable: CreateTable
    extensions: ExtensionTable list }

type NormalizedSchemaError =
  | NullableColumnsDetected of table: string * columns: string list
  | InvalidForeignKey of extension: string * expected: string * reason: string
  | InvalidNaming of table: string * expected: string
  | ForeignKeyNotPrimaryKey of extension: string * fkColumns: string list
  | DuplicateColumnNames of extension: string * baseTable: string * columns: string list

let hasNullableColumns (table: CreateTable) : bool =
  table.columns |> List.exists TypeGenerator.isColumnNullable

let private getPrimaryKeyColumns (table: CreateTable) : string list =
  let tableLevelPk =
    table.constraints
    |> List.tryPick (fun c ->
      match c with
      | PrimaryKey pk when pk.columns.Length > 0 -> Some pk.columns
      | _ -> None)

  match tableLevelPk with
  | Some columns -> columns
  | None ->
    table.columns
    |> List.filter (fun col ->
      col.constraints
      |> List.exists (fun c ->
        match c with
        | PrimaryKey _ -> true
        | _ -> false))
    |> List.map (fun col -> col.name)

let private sameColumnSet (left: string list) (right: string list) : bool =
  let normalize columns =
    columns |> List.map (fun (column: string) -> column.ToLowerInvariant())

  normalize left = normalize right

let private getForeignKeyInfo (table: CreateTable) : ForeignKey list =
  let columnFks =
    table.columns
    |> List.choose (fun col ->
      col.constraints
      |> List.tryPick (fun c ->
        match c with
        | ForeignKey fk ->
          Some
            { columns = [ col.name ]
              refTable = fk.refTable
              refColumns = fk.refColumns
              onDelete = fk.onDelete
              onUpdate = fk.onUpdate }
        | _ -> None))

  let tableFks =
    table.constraints
    |> List.choose (fun c ->
      match c with
      | ForeignKey fk -> Some fk
      | _ -> None)

  columnFks @ tableFks

let private isPrimaryKeyAlsoForeignKey (table: CreateTable) : (string list * ForeignKey) option =
  let pkCols = getPrimaryKeyColumns table

  if pkCols.IsEmpty then
    None
  else
    let fks = getForeignKeyInfo table

    fks
    |> List.tryFind (fun fk -> sameColumnSet fk.columns pkCols)
    |> Option.map (fun fk -> pkCols, fk)

let private extractAspectName (baseName: string) (extensionName: string) : string option =
  let prefix = baseName + "_"

  if extensionName.StartsWith prefix && extensionName.Length > prefix.Length then
    Some(extensionName.Substring prefix.Length)
  else
    None

let private tryMatchExtensionTable (baseTable: CreateTable) (potentialExtension: CreateTable) : ExtensionTable option =
  if hasNullableColumns potentialExtension then
    None
  else
    match extractAspectName baseTable.name potentialExtension.name with
    | None -> None
    | Some aspectName ->
      match isPrimaryKeyAlsoForeignKey potentialExtension with
      | None -> None
      | Some(fkCols, fk) when fk.refTable = baseTable.name ->
        Some
          { table = potentialExtension
            aspectName = aspectName
            fkColumns = fkCols }
      | Some _ -> None

let findExtensionTables (baseTable: CreateTable) (allTables: CreateTable list) : ExtensionTable list =
  allTables
  |> List.filter (fun table -> table.name <> baseTable.name)
  |> List.choose (tryMatchExtensionTable baseTable)

let detectNormalizedTables (tables: CreateTable list) : NormalizedTable list =
  tables
  |> List.choose (fun table ->
    if hasNullableColumns table then
      None
    else
      let extensions = findExtensionTables table tables

      match extensions with
      | [] -> None
      | _ ->
        Some
          { baseTable = table
            extensions = extensions })

let classifyTables (tables: CreateTable list) : NormalizedTable list * CreateTable list =
  let normalized = detectNormalizedTables tables

  let normalizedTableNames =
    normalized
    |> List.collect (fun table ->
      table.baseTable.name
      :: (table.extensions |> List.map (fun ext -> ext.table.name)))
    |> Set.ofList

  let regular =
    tables
    |> List.filter (fun table -> not (Set.contains table.name normalizedTableNames))

  normalized, regular

let formatError (error: NormalizedSchemaError) : string =
  match error with
  | NullableColumnsDetected(table, columns) ->
    let columnList = columns |> String.concat ", "

    $"Table '{table}' has nullable columns: {columnList}\n"
    + "  Suggestion: Add NOT NULL constraint to these columns:\n"
    + (columns
       |> List.map (fun col -> $"    ALTER TABLE {table} ALTER COLUMN {col} SET NOT NULL;")
       |> String.concat "\n")
  | InvalidForeignKey(extension, expected, reason) ->
    $"Extension table '{extension}' has invalid foreign key to '{expected}': {reason}\n"
    + $"  Suggestion: Ensure the table has a FOREIGN KEY constraint referencing {expected}(id)"
  | InvalidNaming(table, expected) ->
    $"Table '{table}' doesn't follow naming convention\n"
    + $"  Expected: '{expected}_<aspect>' (e.g., '{expected}_address', '{expected}_email_phone')\n"
    + $"  Suggestion: Rename the table to follow the pattern: '{expected}_<aspect>'"
  | ForeignKeyNotPrimaryKey(extension, fkColumns) ->
    let fkColumnText = String.concat ", " fkColumns

    $"Extension table '{extension}' has FK columns '{fkColumnText}' that are not the PRIMARY KEY\n"
    + "  For 1:1 relationships, the FK columns must also be the PK (enforces at most one extension per base record)\n"
    + $"  Suggestion: Make '{fkColumnText}' the PRIMARY KEY of table '{extension}'"
  | DuplicateColumnNames(extension, baseTable, columns) ->
    let columnList = columns |> String.concat ", "

    $"Extension table '{extension}' has columns with same names as base table '{baseTable}': {columnList}\n"
    + "  F# discriminated unions cannot have duplicate field names in a single case.\n"
    + $"  Suggestion: Rename the duplicate columns in '{extension}' to be unique, e.g.:\n"
    + (columns
       |> List.map (fun col -> $"    {col} -> {extension}_{col}")
       |> String.concat "\n")

let private getNullableColumns (table: CreateTable) : string list =
  table.columns
  |> List.filter TypeGenerator.isColumnNullable
  |> List.map (fun col -> col.name)

let private validateExtensionTable
  (baseTable: CreateTable)
  (potentialExtension: CreateTable)
  : Result<ExtensionTable option, NormalizedSchemaError> =
  match extractAspectName baseTable.name potentialExtension.name with
  | None -> Ok None
  | Some aspectName ->
    let nullableCols = getNullableColumns potentialExtension

    if not (List.isEmpty nullableCols) then
      Error(NullableColumnsDetected(potentialExtension.name, nullableCols))
    else
      match isPrimaryKeyAlsoForeignKey potentialExtension with
      | None -> Error(ForeignKeyNotPrimaryKey(potentialExtension.name, getPrimaryKeyColumns potentialExtension))
      | Some(fkCols, fk) ->
        if fk.refTable <> baseTable.name then
          Error(
            InvalidForeignKey(
              potentialExtension.name,
              baseTable.name,
              $"FK references '{fk.refTable}' instead of '{baseTable.name}'"
            )
          )
        else
          let baseColumnNames =
            baseTable.columns |> List.map (fun col -> col.name) |> Set.ofList

          let extensionColumnNames =
            potentialExtension.columns
            |> List.filter (fun col -> not (List.contains col.name fkCols))
            |> List.map (fun col -> col.name)

          let duplicates =
            extensionColumnNames
            |> List.filter (fun name -> Set.contains name baseColumnNames)

          if not (List.isEmpty duplicates) then
            Error(DuplicateColumnNames(potentialExtension.name, baseTable.name, duplicates))
          else
            Ok(
              Some
                { table = potentialExtension
                  aspectName = aspectName
                  fkColumns = fkCols }
            )

let validateExtensionTables
  (baseTable: CreateTable)
  (allTables: CreateTable list)
  : Result<ExtensionTable list, NormalizedSchemaError list> =
  let results =
    allTables
    |> List.filter (fun table -> table.name <> baseTable.name)
    |> List.map (validateExtensionTable baseTable)

  let errors =
    results
    |> List.choose (fun result ->
      match result with
      | Error error -> Some error
      | _ -> None)

  let extensions =
    results
    |> List.choose (fun result ->
      match result with
      | Ok(Some extensionTable) -> Some extensionTable
      | _ -> None)

  if List.isEmpty errors then Ok extensions else Error errors

let validateNormalizedTables (tables: CreateTable list) : Result<NormalizedTable list, NormalizedSchemaError list> =
  let results =
    tables
    |> List.map (fun table ->
      let nullableCols = getNullableColumns table

      if not (List.isEmpty nullableCols) then
        Ok None
      else
        match validateExtensionTables table tables with
        | Error errors -> Error errors
        | Ok extensions ->
          match extensions with
          | [] -> Ok None
          | _ ->
            Ok(
              Some
                { baseTable = table
                  extensions = extensions }
            ))

  let allErrors =
    results
    |> List.choose (fun result ->
      match result with
      | Error errors -> Some errors
      | _ -> None)
    |> List.concat

  let normalized =
    results
    |> List.choose (fun result ->
      match result with
      | Ok(Some normalizedTable) -> Some normalizedTable
      | _ -> None)

  if List.isEmpty allErrors then
    Ok normalized
  else
    Error allErrors
