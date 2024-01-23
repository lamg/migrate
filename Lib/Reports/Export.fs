module internal Migrate.Reports.Export

open System.Data
open Migrate
open Migrate.DbProject
open Migrate.Types
open DbUtil

let viewColumns (p: Project) (view: string) : ColumnType list = failwith "not implemented"
// Checks.TypeChecker.checkTypes p.source |> List.filter (fun x -> x.table = view)

let rowReader (xs: ColumnType list) (rd: IDataReader) =
  xs
  |> List.mapi (fun i x ->
    match x.sqlType with
    | SqlInteger -> rd.GetInt32 i |> Integer
    | SqlText -> rd.GetString i |> String
    | SqlReal -> rd.GetDouble i |> Real)

let findRelation (p: Project) (relation: string) =
  let table = p.source.tables |> List.tryFind (fun t -> t.name = relation)

  match table with
  | Some t -> Some(Choice1Of2 t)
  | None ->
    let view = p.source.views |> List.tryFind (fun v -> v.name = relation)

    match view with
    | Some v -> Some(Choice2Of2 v)
    | None -> None

let exportTable (p: Project) (table: CreateTable) =
  use conn = openConn p.dbFile

  LoadDbSchema.tableValues conn LoadDbSchema.allRows table
  |> SqlGeneration.InsertInto.sqlInsertInto
  |> joinSqlPretty

let exportView (p: Project) (view: string) =
  let cols = viewColumns p view
  let colNames = cols |> List.map _.column
  let rd = rowReader cols
  use conn = openConn p.dbFile

  LoadDbSchema.allRows conn view colNames rd
  |> SqlGeneration.InsertInto.sqlInsertInto
  |> joinSqlPretty

let exportRelation (p: Project) (relation: string) =
  findRelation p relation
  |> Option.map (function
    | Choice1Of2 table -> exportTable p table
    | Choice2Of2 view -> exportView p view.name)
