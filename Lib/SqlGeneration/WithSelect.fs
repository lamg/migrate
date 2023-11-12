module Migrate.SqlGeneration.WithSelect

open SqlParser.Types
open Util
open Migrate.SqlGeneration.Expr

let rec sqlOrderBy: OrderBy option -> string =
  function
  | Some o ->
    let ascDesc =
      match o.asc with
      | true -> "ASC"
      | false -> "DESC"

    let vars = sepComma sqlVar o.columns
    $" ORDER BY {vars} {ascDesc}"
  | None -> ""

and sqlWhere: Expr option -> string =
  function
  | Some w -> $" WHERE {sqlExpr sqlWithSelect w}"
  | None -> ""

and sqlHaving: Expr option -> string =
  function
  | Some h -> $" HAVING {sqlExpr sqlWithSelect h}"
  | None -> ""

and sqlSelect (s: Select) =
  let cols =
    match s.columns with
    | [] -> " *"
    | _ -> $" {s.columns |> sepComma (sqlExpr sqlWithSelect)}"

  let from =
    match s.from with
    | [] -> ""
    | _ -> $" FROM {s.from |> sepComma (sqlExpr sqlWithSelect)}"

  let where = sqlWhere s.where

  let groupBy =
    match s.groupBy with
    | [] -> ""
    | _ -> $" GROUP BY {s.groupBy |> sepComma sqlVar}"

  let having = sqlHaving s.having
  let orderBy = sqlOrderBy s.orderBy
  let limit = s.limit |> Option.map (fun l -> $" LIMIT {l}") |> Option.defaultValue ""

  let offset =
    s.offset |> Option.map (fun o -> $" OFFSET {o}") |> Option.defaultValue ""

  $"SELECT{cols}{from}{where}{groupBy}{having}{orderBy}{limit}{offset}"

and sqlWithSelect (g: WithSelect) =
  match g.withAliases with
  | [] -> sqlSelect g.select
  | _ ->
    let aliases =
      g.withAliases |> sepCommaNl (fun a -> $"{a.alias} AS ({sqlSelect a.select})")

    $"WITH {aliases}\n{sqlSelect g.select}"
