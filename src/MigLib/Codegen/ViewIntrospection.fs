module internal MigLib.Codegen.ViewIntrospection

open System
open Microsoft.Data.Sqlite
open MigLib.Schema.Types
open MigLib.TaskResult

let getViewColumns (tables: CreateTable list) (view: CreateView) : Result<ViewColumn list, string> =
  result {
    use conn = new SqliteConnection "Data Source=:memory:"
    conn.Open()

    for table in tables do
      let createTableSql =
        let columns =
          table.columns
          |> List.map (fun col ->
            let typeName =
              match col.columnType with
              | SqlInteger -> "INTEGER"
              | SqlText -> "TEXT"
              | SqlReal -> "REAL"
              | SqlTimestamp -> "TIMESTAMP"
              | SqlString -> "TEXT"

            let constraints =
              col.constraints
              |> List.choose (fun c ->
                match c with
                | NotNull -> Some "NOT NULL"
                | PrimaryKey _ -> Some "PRIMARY KEY"
                | Autoincrement -> Some "AUTOINCREMENT"
                | _ -> None)
              |> String.concat " "

            if String.IsNullOrWhiteSpace constraints then
              $"{col.name} {typeName}"
            else
              $"{col.name} {typeName} {constraints}")
          |> String.concat ", "

        $"CREATE TABLE {table.name} ({columns})"

      use cmd = new SqliteCommand(createTableSql, conn)
      cmd.ExecuteNonQuery() |> ignore

    use viewCmd = new SqliteCommand(view.sql, conn)

    try
      viewCmd.ExecuteNonQuery() |> ignore
    with ex ->
      return! Error $"Failed to create view {view.name}: {ex.Message}"

    use pragmaCmd = new SqliteCommand($"PRAGMA table_info({view.name})", conn)
    use reader = pragmaCmd.ExecuteReader()

    let columns = ResizeArray<ViewColumn>()

    while reader.Read() do
      let colName = reader.GetString 1
      let colType = reader.GetString 2

      let sqlType =
        match colType.ToUpperInvariant() with
        | t when t.Contains "INT" -> SqlInteger
        | t when t.Contains "TEXT" || t.Contains "CHAR" || t.Contains "CLOB" -> SqlText
        | t when t.Contains "REAL" || t.Contains "FLOA" || t.Contains "DOUB" -> SqlReal
        | t when t.Contains "TIME" || t.Contains "DATE" -> SqlTimestamp
        | _ -> SqlText

      let declaredColumn =
        view.declaredColumns
        |> List.tryFind (fun declared -> String.Equals(declared.name, colName, StringComparison.OrdinalIgnoreCase))

      let resolvedColumnType =
        match declaredColumn with
        | Some column -> column.columnType
        | None -> sqlType

      columns.Add
        { name = colName
          columnType = resolvedColumnType
          enumLikeDu = declaredColumn |> Option.bind _.enumLikeDu
          unitOfMeasure = declaredColumn |> Option.bind _.unitOfMeasure }

    let introspectedColumns = columns |> Seq.toList

    return
      if introspectedColumns.IsEmpty && not view.declaredColumns.IsEmpty then
        view.declaredColumns
      else
        introspectedColumns
  }
