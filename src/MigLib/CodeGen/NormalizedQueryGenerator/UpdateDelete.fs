module internal Mig.CodeGen.NormalizedQueryGeneratorUpdateDelete

open Mig.DeclarativeMigrations.Types
open Fabulous.AST
open type Fabulous.AST.Ast
open Mig.CodeGen.AstExprBuilders
open Mig.CodeGen.NormalizedSchema
open Mig.CodeGen.NormalizedQueryGeneratorCommon
open Mig.CodeGen.SqlParamBindings

let private generateUpdateBaseSql (baseTable: CreateTable) : string =
  let pkCols =
    getPrimaryKeyColumns baseTable |> List.map (fun c -> c.name) |> Set.ofList

  let updateCols =
    baseTable.columns |> List.filter (fun col -> not (Set.contains col.name pkCols))

  let setClauses =
    updateCols |> List.map (fun c -> $"{c.name} = @{c.name}") |> String.concat ", "

  let whereClause =
    pkCols
    |> Set.toList
    |> List.map (fun pk -> $"{pk} = @{pk}")
    |> String.concat " AND "

  $"UPDATE {baseTable.name} SET {setClauses} WHERE {whereClause}"

let private generateUpdateBaseCase
  (baseTable: CreateTable)
  (extensions: ExtensionTable list)
  (typeName: string)
  : string =
  let updateSql = generateUpdateBaseSql baseTable
  let fieldPattern = generateFieldPattern baseTable.columns

  let idCol =
    baseTable.columns
    |> List.find (fun col ->
      col.constraints
      |> List.exists (function
        | PrimaryKey _ -> true
        | _ -> false))

  let idFieldName = TypeGenerator.toPascalCase idCol.name

  let idVarName =
    idFieldName.ToLower().[0..0]
    + (if idFieldName.Length > 1 then idFieldName.[1..] else "")

  let asyncParamBindings =
    generateParamBindings baseTable.columns "cmd"
    |> String.concat "\n                "

  let deleteStatements =
    extensions
    |> List.mapi (fun i ext ->
      let deleteResultName = $"deleteResult{i}"
      let deleteIdBinding = addPlainBinding "cmd" "id" idVarName

      $"            let! {deleteResultName} =\n              executeWrite\n                \"DELETE FROM {ext.table.name} WHERE {ext.fkColumn} = @id\"\n                (fun cmd ->\n                  {deleteIdBinding})\n                tx\n                (fun _ -> task {{ return Ok() }})\n\n            match {deleteResultName} with\n            | Error ex -> return Error ex\n            | Ok () -> ()")
    |> String.concat "\n\n"

  $"""        | {typeName}.Base({fieldPattern}) ->
          // Update base table, delete all extensions
          let! updateResult =
            executeWrite
              "{updateSql}"
              (fun cmd ->
                {asyncParamBindings})
              tx
              (fun _ -> task {{ return Ok() }})

          match updateResult with
          | Error ex -> return Error ex
          | Ok () ->
{deleteStatements}
            return Ok()"""

let private generateUpdateExtensionCase
  (baseTable: CreateTable)
  (extension: ExtensionTable)
  (allExtensions: ExtensionTable list)
  (typeName: string)
  : string =
  let caseName = TypeGenerator.toPascalCase extension.aspectName
  let updateSql = generateUpdateBaseSql baseTable

  let extensionInsertColumns =
    extension.table.columns
    |> List.filter (fun col -> col.name <> extension.fkColumn)

  let extensionColumnNames =
    extensionInsertColumns |> List.map (fun c -> c.name) |> String.concat ", "

  let extensionParamNames =
    extensionInsertColumns |> List.map (fun c -> $"@{c.name}") |> String.concat ", "

  let insertOrReplaceSql =
    $"INSERT OR REPLACE INTO {extension.table.name} ({extension.fkColumn}, {extensionColumnNames}) VALUES (@{extension.fkColumn}, {extensionParamNames})"

  let allColumns = baseTable.columns @ extensionInsertColumns
  let fieldPattern = generateFieldPattern allColumns

  let idCol =
    baseTable.columns
    |> List.find (fun col ->
      col.constraints
      |> List.exists (function
        | PrimaryKey _ -> true
        | _ -> false))

  let idFieldName = TypeGenerator.toPascalCase idCol.name

  let idVarName =
    idFieldName.ToLower().[0..0]
    + (if idFieldName.Length > 1 then idFieldName.[1..] else "")

  let asyncBaseParamBindings =
    generateParamBindings baseTable.columns "cmd"
    |> String.concat "\n                "

  let asyncExtensionParamBindings =
    generateParamBindings extensionInsertColumns "cmd"
    |> String.concat "\n                    "

  let extensionFkBinding = addPlainBinding "cmd" extension.fkColumn idVarName

  let deleteOtherExtensions =
    allExtensions
    |> List.filter (fun e -> e.table.name <> extension.table.name)
    |> List.mapi (fun i ext ->
      let deleteResultName = $"deleteOtherResult{i}"
      let deleteIdBinding = addPlainBinding "cmd" "id" idVarName

      $"                let! {deleteResultName} =\n                  executeWrite\n                    \"DELETE FROM {ext.table.name} WHERE {ext.fkColumn} = @id\"\n                    (fun cmd ->\n                      {deleteIdBinding})\n                    tx\n                    (fun _ -> task {{ return Ok() }})\n\n                match {deleteResultName} with\n                | Error ex -> return Error ex\n                | Ok () -> ()")
    |> String.concat "\n\n"

  $"""        | {typeName}.With{caseName}({fieldPattern}) ->
          // Update base, INSERT OR REPLACE extension
          let! updateBaseResult =
            executeWrite
              "{updateSql}"
              (fun cmd ->
                {asyncBaseParamBindings})
              tx
              (fun _ -> task {{ return Ok() }})

          match updateBaseResult with
          | Error ex -> return Error ex
          | Ok () ->
            let! updateExtensionResult =
              executeWrite
                "{insertOrReplaceSql}"
                (fun cmd ->
                  {extensionFkBinding}
                  {asyncExtensionParamBindings})
                tx
                (fun _ -> task {{ return Ok() }})

            match updateExtensionResult with
            | Error ex -> return Error ex
            | Ok () ->
{deleteOtherExtensions}
              return Ok()"""

let generateUpdate (normalized: NormalizedTable) : string option =
  let pkCols = getPrimaryKeyColumns normalized.baseTable

  match pkCols with
  | [] -> None
  | _ ->
    let typeName = TypeGenerator.toPascalCase normalized.baseTable.name

    let baseCase =
      generateUpdateBaseCase normalized.baseTable normalized.extensions typeName

    let extensionCases =
      normalized.extensions
      |> List.map (fun ext -> generateUpdateExtensionCase normalized.baseTable ext normalized.extensions typeName)
      |> String.concat "\n\n"

    let allCases =
      if normalized.extensions.IsEmpty then
        baseCase
      else
        $"{baseCase}\n\n{extensionCases}"

    Some
      $"""  static member Update (item: {typeName}) (tx: SqliteTransaction)
    : Task<Result<unit, SqliteException>> =
    task {{
      try
        match item with
{allCases}
      with
      | :? SqliteException as ex -> return Error ex
    }}"""

let generateDelete (normalized: NormalizedTable) : string option =
  let pkCols = getPrimaryKeyColumns normalized.baseTable

  match pkCols with
  | [] -> None
  | pks ->
    let typeName = TypeGenerator.toPascalCase normalized.baseTable.name

    let whereClause =
      pks |> List.map (fun pk -> $"{pk.name} = @{pk.name}") |> String.concat " AND "

    let deleteSql = $"DELETE FROM {normalized.baseTable.name} WHERE {whereClause}"

    let paramList =
      pks
      |> List.map (fun pk -> let pkType = TypeGenerator.mapColumnType pk in $"({pk.name}: {pkType})")
      |> String.concat " "

    let asyncParamBindings =
      pks
      |> List.map (fun pk -> addColumnBinding "cmd" pk pk.name)
      |> joinBindings "        "

    Some
      $"""  static member Delete {paramList} (tx: SqliteTransaction) : Task<Result<unit, SqliteException>> =
    executeWrite
      "{deleteSql}"
      (fun cmd ->
        {asyncParamBindings})
      tx
      (fun _ ->
        task {{
          return Ok()
        }})"""
