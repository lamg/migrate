module internal MigLib.Commands.Migrate.DataCopy

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Data.Sqlite

open MigLib.Commands.Schema.Types
open MigLib.Commands.Types

type CopyResult =
  { copiedTables: int; copiedRows: int64 }

let private sqliteInitialized = lazy (SQLitePCL.Batteries_V2.Init())

let private ensureSqliteInitialized () = sqliteInitialized.Force()

let private quoteIdentifier (identifier: string) =
  let escaped = identifier.Replace("\"", "\"\"")
  $"\"{escaped}\""

let private sourceTableName (table: CreateTable) =
  defaultArg table.previousName table.name

let private sourceColumnName (column: ColumnDef) =
  defaultArg column.previousName column.name

let private openSqliteConnection dbPath =
  ensureSqliteInitialized ()
  let connection = new SqliteConnection($"Data Source={dbPath}")
  connection.Open()
  connection

let private createCommand (connection: SqliteConnection) (tx: SqliteTransaction) sql =
  new SqliteCommand(sql, connection, tx)

let private sourceTableExpression tableName =
  let sourceDbName = quoteIdentifier "source_db"
  $"{sourceDbName}.{quoteIdentifier tableName}"

let private attachSourceDatabase (connection: SqliteConnection) (tx: SqliteTransaction) sourceDbPath =
  task {
    let sourceDbName = quoteIdentifier "source_db"

    use command =
      createCommand connection tx $"ATTACH DATABASE @sourcePath AS {sourceDbName};"

    command.Parameters.AddWithValue("@sourcePath", Path.GetFullPath sourceDbPath)
    |> ignore

    let! _ = command.ExecuteNonQueryAsync()
    return ()
  }

let private copyMappedColumns
  (connection: SqliteConnection)
  (tx: SqliteTransaction)
  (sourceTable: CreateTable)
  (targetTable: CreateTable)
  =
  task {
    let sourceColumnNames = sourceTable.columns |> List.map _.name |> Set.ofList

    let mappedColumns =
      targetTable.columns
      |> List.choose (fun targetColumn ->
        let sourceName = sourceColumnName targetColumn

        if sourceColumnNames.Contains sourceName then
          Some(targetColumn.name, sourceName)
        else
          None)

    if mappedColumns.IsEmpty then
      return 0L
    else
      let targetColumns =
        mappedColumns |> List.map (fst >> quoteIdentifier) |> String.concat ", "

      let sourceColumns =
        mappedColumns |> List.map (snd >> quoteIdentifier) |> String.concat ", "

      let sql =
        $"INSERT INTO {quoteIdentifier targetTable.name} ({targetColumns}) SELECT {sourceColumns} FROM {sourceTableExpression sourceTable.name};"

      use command = createCommand connection tx sql
      let! rows = command.ExecuteNonQueryAsync()
      return int64 rows
  }

let copyData
  (reportProgress: ProgReport)
  (sourceDbPath: string)
  (targetDbPath: string)
  (sourceSchema: SqlFile)
  (targetSchema: SqlFile)
  : Task<Result<CopyResult, MigError>> =
  task {
    try
      do! reportProgress $"Copying data from source database: {sourceDbPath}"
      use connection = openSqliteConnection targetDbPath
      use tx = connection.BeginTransaction()

      do! attachSourceDatabase connection tx sourceDbPath

      let sourceTableByName =
        sourceSchema.tables |> List.map (fun table -> table.name, table) |> Map.ofList

      let mutable copiedTables = 0
      let mutable copiedRows = 0L

      for targetTable in targetSchema.tables do
        match sourceTableByName.TryFind(sourceTableName targetTable) with
        | None -> ()
        | Some sourceTable ->
          do! reportProgress $"Copying table: {sourceTable.name} -> {targetTable.name}"
          let! rows = copyMappedColumns connection tx sourceTable targetTable
          do! reportProgress $"Copied {rows} row(s) into table: {targetTable.name}"
          copiedTables <- copiedTables + 1
          copiedRows <- copiedRows + rows

      tx.Commit()
      do! reportProgress $"Copied {copiedRows} row(s) across {copiedTables} table(s)."

      return
        Ok
          { copiedTables = copiedTables
            copiedRows = copiedRows }
    with
    | :? SqliteException as ex -> return Error(MigError.Sqlite ex)
    | ex -> return Error(MigError.Other ex)
  }
