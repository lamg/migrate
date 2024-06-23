// Copyright 2023 Luis Ángel Méndez Gort

// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at

//     http://www.apache.org/licenses/LICENSE-2.0

// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

module Migrate.Checks.Types

open Migrate
open Migrate.Types
open SqlParser.Ast

type Ident =
  { qualifier: string option
    alias: string option
    name: string }

type ColumnId = { table: string; name: string }

type Types = Map<ColumnId, SqlType>

type TypeConnections = Map<ColumnId, ColumnId>

type Relation =
  { name: string
    columns: list<string * SqlType> }

let fromToId (ts: TableWithJoins seq) =
  ts
  |> Seq.map (fun r ->
    seq {
      let t = r.Relation.AsTable()

      yield
        { alias = Option.ofObj t.Alias |> Option.map (fun a -> a.Name.Value)
          qualifier = None
          name = t.Name.Values |> Seq.head |> _.Value }

      yield!
        r.Joins
        |> Option.ofObj
        |> Option.map (
          Seq.map (fun s ->
            let nt = s.Relation.AsTable()

            { qualifier = None
              alias = Option.ofObj nt.Alias.Name.Value
              name = nt.Name.Values |> Seq.head |> _.Value })
        )
        |> Option.defaultValue []
    })
  |> Seq.concat
  |> Seq.toList

#nowarn "25"

let projectionToId (xs: SelectItem seq) =
  let extractId alias e =
    match box e with
    | :? Expression.Identifier as r ->

      { qualifier = None
        alias = alias
        name = r.Ident.Value }
    | :? Expression.CompoundIdentifier as r ->
      let [ x; y ] = r.Idents |> Seq.map (fun v -> v.Value) |> Seq.take 2 |> Seq.toList

      { qualifier = Some x
        name = y
        alias = alias }
    | v -> failwith $"unexected value {v}"

  xs
  |> Seq.map (fun x ->
    match box x with
    | :? SelectItem.UnnamedExpression as e -> extractId None e.Expression
    | :? SelectItem.ExpressionWithAlias as e -> extractId (Some e.Alias.Value) e.Expression
    | :? SelectItem.Wildcard -> failwith "TODO all identifiers in from FROM expression"
    | v -> failwith $"not implemented for {v}")
  |> Seq.toList

let findConnections (viewName: string) (s: Select) =
  let ps = projectionToId s.Projection
  let fs = fromToId s.From

  // each column in a select can be qualified or not
  // qualified columns identify their table using the qualifier
  // the qualifier points to an alias of the table, or to the table name itself
  // unqualified columns can be only matched when there's a single table in the
  // FROM expression
  let findTable (qualifier: string option) =
    match qualifier, fs with
    | None, [ f ] -> Ok f
    | None, [] -> Error "expecting a non-empty FROM expression"
    | None, _ -> Error "expecting a FROM expression with only one member"
    | Some q, _ ->
      fs
      |> List.tryFind (function
        | { alias = Some a } when a = q -> true
        | { name = n } -> n = q)
      |> function
        | Some r -> Ok r
        | None -> Error $"not found {q} in {fs}"

  ps
  |> List.fold
    (fun (oks, errs) p ->
      match findTable p.qualifier with
      | Ok t ->
        let conn =
          { table = viewName
            name = p.alias |> Option.defaultValue p.name },
          { table = t.name; name = p.name }

        (conn :: oks, errs)
      | Error e -> (oks, e :: errs))
    ([], [])
  |> function
    | oks, errs -> ((Map.ofList oks): TypeConnections), errs

let initialTypes (cts: CreateTable list) : Types =
  cts
  |> Seq.map (fun ct ->
    ct.columns
    |> List.map (fun c -> { table = ct.name; name = c.name }, c.columnType))
  |> Seq.concat
  |> Map.ofSeq

let viewTypeConnections (cvs: CreateView list) =
  cvs
  |> List.fold
    (fun (conns, errs) cv ->
      let ns, es = findConnections cv.name cv.selectUnion

      ns
      |> Map.fold
        (fun (ms: Map<ColumnId, ColumnId>, nes) k v ->
          if ms.ContainsKey k then
            (ms, $"duplicated key {k}" :: nes)
          else
            (Map.add k v ms, nes))
        (conns, es @ errs))
    (Map.empty, [])

let rec findType (types: Types, cs: TypeConnections) (c: ColumnId) =
  match types.TryGetValue c with
  | true, d -> Some d
  | _ ->
    match cs.TryGetValue c with
    | true, e -> findType (types, cs) e
    | _ -> None

let typeCheck (s: SqlFile) =
  let initial = initialTypes s.tables
  let conns, errs = viewTypeConnections s.views

  match errs with
  | [] ->
    conns.Keys
    |> Seq.fold
      (fun (ts, errs) k ->
        match findType (ts, conns) k with
        | Some v -> Map.add k v ts, errs
        | None ->
          //conns |> Map.iter (fun k v -> printfn $"k = {k} v = {v}")
          ts, $"type not found for {k}" :: errs)
      (initial, [])

  | _ -> initial, errs

let relationTypes (types: Types) =
  types
  |> Map.toList
  |> List.groupBy (fun (k, _) -> k.table)
  |> List.map (fun (table, cols) ->
    { name = table
      columns = cols |> List.map (fun (c, t) -> c.name, t) })
