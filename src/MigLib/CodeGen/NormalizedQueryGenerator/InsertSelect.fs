module internal Mig.CodeGen.NormalizedQueryGeneratorInsertSelect

open Mig.DeclarativeMigrations.Types
open Mig.CodeGen.NormalizedSchema
open Mig.CodeGen.NormalizedQueryGeneratorCommon
open Mig.CodeGen.SqlParamBindings

let private generateBaseCase (baseTable: CreateTable) (typeName: string) : string =
  let insertColumns = getInsertColumns baseTable
  let insertSql = generateInsertSql baseTable.name insertColumns
  let fieldPattern = generateFieldPattern insertColumns

  let asyncParamBindings =
    generateParamBindings insertColumns "cmd" |> String.concat "\n                "

  $"""        | New{typeName}.Base({fieldPattern}) ->
          // Single INSERT into base table
          return!
            executeWrite
              "{insertSql}"
              (fun cmd ->
                {asyncParamBindings})
              tx
              (fun _ ->
                task {{
                  use lastIdCmd = new SqliteCommand("SELECT last_insert_rowid()", tx.Connection, tx)
                  let! lastId = lastIdCmd.ExecuteScalarAsync()
                  let {baseTable.name}Id = lastId |> unbox<int64>
                  return Ok {baseTable.name}Id
                }})"""

let private generateBaseCaseInsertOrIgnore (baseTable: CreateTable) (typeName: string) : string =
  let insertColumns = getInsertColumns baseTable
  let insertSql = generateInsertOrIgnoreSql baseTable.name insertColumns
  let fieldPattern = generateFieldPattern insertColumns

  let asyncParamBindings =
    generateParamBindings insertColumns "cmd" |> String.concat "\n                "

  $"""        | New{typeName}.Base({fieldPattern}) ->
          // Single INSERT OR IGNORE into base table
          return!
            executeWrite
              "{insertSql}"
              (fun cmd ->
                {asyncParamBindings})
              tx
              (fun rows ->
                task {{
                  if rows = 0 then
                    return Ok None
                  else
                    use lastIdCmd = new SqliteCommand("SELECT last_insert_rowid()", tx.Connection, tx)
                    let! lastId = lastIdCmd.ExecuteScalarAsync()
                    let {baseTable.name}Id = lastId |> unbox<int64>
                    return Ok (Some {baseTable.name}Id)
                }})"""

let private generateExtensionCase (baseTable: CreateTable) (extension: ExtensionTable) (typeName: string) : string =
  let caseName = TypeGenerator.toPascalCase extension.aspectName
  let baseInsertColumns = getInsertColumns baseTable
  let baseInsertSql = generateInsertSql baseTable.name baseInsertColumns
  let basePkColumn = getSinglePrimaryKeyColumn baseTable

  let extensionFkValueExpr =
    match basePkColumn with
    | Some pkCol when not (isAutoIncrementPrimaryKey pkCol) -> getColumnVarName pkCol
    | _ -> $"{baseTable.name}Id"

  let extensionInsertColumns =
    extension.table.columns
    |> List.filter (fun col -> col.name <> extension.fkColumn)

  let allColumns = baseInsertColumns @ extensionInsertColumns
  let fieldPattern = generateFieldPattern allColumns

  let asyncBaseParamBindings =
    generateParamBindings baseInsertColumns "cmd"
    |> String.concat "\n                "

  let asyncExtensionParamBindings =
    generateParamBindings extensionInsertColumns "cmd"
    |> String.concat "\n                    "

  let extensionFkBinding =
    addPlainBinding "cmd" extension.fkColumn extensionFkValueExpr

  $"""        | New{typeName}.With{caseName}({fieldPattern}) ->
          // Two inserts in same transaction (atomic)
          let! baseInsertResult =
            executeWrite
              "{baseInsertSql}"
              (fun cmd ->
                {asyncBaseParamBindings})
              tx
              (fun _ ->
                task {{
                  use lastIdCmd = new SqliteCommand("SELECT last_insert_rowid()", tx.Connection, tx)
                  let! lastId = lastIdCmd.ExecuteScalarAsync()
                  let {baseTable.name}Id = lastId |> unbox<int64>
                  return Ok {baseTable.name}Id
                }})

          match baseInsertResult with
          | Error ex -> return Error ex
          | Ok {baseTable.name}Id ->
            return!
              executeWrite
                "INSERT INTO {extension.table.name} ({extension.fkColumn}, {extensionInsertColumns |> List.map (fun c -> c.name) |> String.concat ", "}) VALUES (@{extension.fkColumn}, {extensionInsertColumns |> List.map (fun c -> $"@{c.name}") |> String.concat ", "})"
                (fun cmd ->
                  {extensionFkBinding}
                  {asyncExtensionParamBindings})
                tx
                (fun _ ->
                  task {{
                    return Ok {baseTable.name}Id
                  }})"""

let private generateExtensionCaseInsertOrIgnore
  (baseTable: CreateTable)
  (extension: ExtensionTable)
  (typeName: string)
  : string =
  let caseName = TypeGenerator.toPascalCase extension.aspectName
  let baseInsertColumns = getInsertColumns baseTable
  let baseInsertSql = generateInsertOrIgnoreSql baseTable.name baseInsertColumns
  let basePkColumn = getSinglePrimaryKeyColumn baseTable

  let extensionFkValueExpr =
    match basePkColumn with
    | Some pkCol when not (isAutoIncrementPrimaryKey pkCol) -> getColumnVarName pkCol
    | _ -> $"{baseTable.name}Id"

  let extensionInsertColumns =
    extension.table.columns
    |> List.filter (fun col -> col.name <> extension.fkColumn)

  let allColumns = baseInsertColumns @ extensionInsertColumns
  let fieldPattern = generateFieldPattern allColumns

  let asyncBaseParamBindings =
    generateParamBindings baseInsertColumns "cmd"
    |> String.concat "\n                "

  let asyncExtensionParamBindings =
    generateParamBindings extensionInsertColumns "cmd"
    |> String.concat "\n                    "

  let extensionFkBinding =
    addPlainBinding "cmd" extension.fkColumn extensionFkValueExpr

  $"""        | New{typeName}.With{caseName}({fieldPattern}) ->
          // Base INSERT OR IGNORE then extension INSERT
          let! baseInsertResult =
            executeWrite
              "{baseInsertSql}"
              (fun cmd ->
                {asyncBaseParamBindings})
              tx
              (fun rows ->
                task {{
                  if rows = 0 then
                    return Ok None
                  else
                    use lastIdCmd = new SqliteCommand("SELECT last_insert_rowid()", tx.Connection, tx)
                    let! lastId = lastIdCmd.ExecuteScalarAsync()
                    let {baseTable.name}Id = lastId |> unbox<int64>
                    return Ok (Some {baseTable.name}Id)
                }})

          match baseInsertResult with
          | Error ex -> return Error ex
          | Ok None -> return Ok None
          | Ok(Some {baseTable.name}Id) ->
            return!
              executeWrite
                "INSERT INTO {extension.table.name} ({extension.fkColumn}, {extensionInsertColumns |> List.map (fun c -> c.name) |> String.concat ", "}) VALUES (@{extension.fkColumn}, {extensionInsertColumns |> List.map (fun c -> $"@{c.name}") |> String.concat ", "})"
                (fun cmd ->
                  {extensionFkBinding}
                  {asyncExtensionParamBindings})
                tx
                (fun _ ->
                  task {{
                    return Ok (Some {baseTable.name}Id)
                  }})"""

let generateInsert (normalized: NormalizedTable) : string =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name
  let baseCase = generateBaseCase normalized.baseTable typeName

  let extensionCases =
    normalized.extensions
    |> List.map (fun ext -> generateExtensionCase normalized.baseTable ext typeName)
    |> String.concat "\n\n"

  let allCases =
    if normalized.extensions.IsEmpty then
      baseCase
    else
      $"{baseCase}\n\n{extensionCases}"

  $"""  static member Insert (item: New{typeName}) (tx: SqliteTransaction)
    : Task<Result<int64, SqliteException>> =
    task {{
      try
        match item with
{allCases}
      with
      | :? SqliteException as ex -> return Error ex
    }}"""

let generateInsertOrIgnore (normalized: NormalizedTable) : string =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name
  let baseCase = generateBaseCaseInsertOrIgnore normalized.baseTable typeName

  let extensionCases =
    normalized.extensions
    |> List.map (fun ext -> generateExtensionCaseInsertOrIgnore normalized.baseTable ext typeName)
    |> String.concat "\n\n"

  let allCases =
    if normalized.extensions.IsEmpty then
      baseCase
    else
      $"{baseCase}\n\n{extensionCases}"

  $"""  static member InsertOrIgnore (item: New{typeName}) (tx: SqliteTransaction)
    : Task<Result<int64 option, SqliteException>> =
    task {{
      try
        match item with
{allCases}
      with
      | :? SqliteException as ex -> return Error ex
    }}"""

let generateGetAll (normalized: NormalizedTable) : string =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name
  let selectColumns = generateSelectColumns normalized.baseTable normalized.extensions

  let leftJoins =
    if normalized.extensions.IsEmpty then
      ""
    else
      "\n         " + generateLeftJoins normalized.baseTable normalized.extensions

  let getSql =
    $"SELECT {selectColumns}\n         FROM {normalized.baseTable.name}{leftJoins}"

  let caseSelection =
    generateCaseSelection 14 normalized.baseTable normalized.extensions typeName

  $"""  static member SelectAll (tx: SqliteTransaction) : Task<Result<{typeName} list, SqliteException>> =
    queryList
      "{getSql}"
      (fun _ -> ())
      (fun reader ->
        let item =
{caseSelection}
        item)
      tx"""

let generateGetById (normalized: NormalizedTable) : string option =
  let pkCols = getPrimaryKeyColumns normalized.baseTable

  match pkCols with
  | [] -> None
  | pks ->
    let typeName = TypeGenerator.toPascalCase normalized.baseTable.name
    let selectColumns = generateSelectColumns normalized.baseTable normalized.extensions

    let leftJoins =
      if normalized.extensions.IsEmpty then
        ""
      else
        "\n         " + generateLeftJoins normalized.baseTable normalized.extensions

    let whereClause =
      pks
      |> List.map (fun pk -> $"{normalized.baseTable.name}.{pk.name} = @{pk.name}")
      |> String.concat " AND "

    let getSql =
      $"SELECT {selectColumns}\n         FROM {normalized.baseTable.name}{leftJoins}\n         WHERE {whereClause}"

    let paramList =
      pks
      |> List.map (fun pk -> let pkType = TypeGenerator.mapColumnType pk in $"({pk.name}: {pkType})")
      |> String.concat " "

    let asyncParamBindings =
      pks
      |> List.map (fun pk -> addColumnBinding "cmd" pk pk.name)
      |> joinBindings "        "

    let caseSelection =
      generateCaseSelection 12 normalized.baseTable normalized.extensions typeName

    Some
      $"""  static member SelectById {paramList} (tx: SqliteTransaction) : Task<Result<{typeName} option, SqliteException>> =
    querySingle
      "{getSql}"
      (fun cmd ->
        {asyncParamBindings})
      (fun reader ->
        let item =
{caseSelection}
        item)
      tx"""

let generateGetOne (normalized: NormalizedTable) : string =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name
  let selectColumns = generateSelectColumns normalized.baseTable normalized.extensions

  let leftJoins =
    if normalized.extensions.IsEmpty then
      ""
    else
      "\n         " + generateLeftJoins normalized.baseTable normalized.extensions

  let getSql =
    $"SELECT {selectColumns}\n         FROM {normalized.baseTable.name}{leftJoins}\n         LIMIT 1"

  let caseSelection =
    generateCaseSelection 12 normalized.baseTable normalized.extensions typeName

  $"""  static member SelectOne (tx: SqliteTransaction) : Task<Result<{typeName} option, SqliteException>> =
    querySingle
      "{getSql}"
      (fun _ -> ())
      (fun reader ->
        let item =
{caseSelection}
        item)
      tx"""
