module Migrate.SqlGeneration.Expr

open Migrate.SqlGeneration
open SqlParser.Types
open Util

let sqlExpr (sqlSubQuery: WithSelect -> string) (e: Expr) =
  let rec loop (e: Expr) =
    match e with
    | Integer c -> $"{c}"
    | String s -> $"'{s}'"
    | Column c -> sqlVar c
    | Table t -> sqlVar t
    | CaseWhen c ->
      let sqlCond = loop c.cond
      let sqlThen = loop c.thenExpr
      let sqlElse = loop c.elseExpr
      $"CASE WHEN {sqlCond} THEN {sqlThen} ELSE {sqlElse} END"
    | Func f ->
      let args = f.args |> sepComma loop
      $"{f.name}({args})"
    | And { left = l; right = r } -> $"{loop l} AND {loop r}"
    | Or { left = l; right = r } -> $"{loop l} OR {loop r}"
    | Not e -> $"NOT {loop e}"
    | Eq { left = l; right = r } -> $"{loop l} = {loop r}"
    | Neq { left = l; right = r } -> $"{loop l} <> {loop r}"
    | Gt { left = l; right = r } -> $"{loop l} > {loop r}"
    | Gte { left = l; right = r } -> $"{loop l} >= {loop r}"
    | Lt { left = l; right = r } -> $"{loop l} < {loop r}"
    | Lte { left = l; right = r } -> $"{loop l} <= {loop r}"
    | In { left = l; right = r } -> $"{loop l} IN {loop r}"
    | Exists e -> $"EXISTS {loop e}"
    | Like { left = l; right = r } -> $"{loop l} LIKE {loop r}"
    | Concat { left = l; right = r } -> $"{loop l} || {loop r}"
    | InnerJoin { left = l; right = r } -> $"{loop l} JOIN {loop r}"
    | LeftOuterJoin { left = l; right = r } -> $"{loop l} LEFT OUTER JOIN {loop r}"
    | JoinOn { relation = join; onExpr = onExpr } -> $"{loop join} ON {loop onExpr}"
    | Alias a -> $"{loop a.expr} AS {a.alias}"
    | SubQuery s -> $"({sqlSubQuery s})"
    | _ -> failwith "not implemented"

  loop e
