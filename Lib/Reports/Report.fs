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

module internal Migrate.Reports.Report

open Migrate.Types
open Migrate.DbUtil
open Migrate.SqlGeneration.Util

let syncReportsConn (p: Project) conn =
  let syncTable { src = src; dest = dest } =
    p.source.tables
    |> List.find (fun t -> t.name = dest)
    |> fun t ->
        let sqlCols = t.columns |> sepComma (fun c -> c.name)

        let sql =
          $"
                DELETE FROM {dest} WHERE TRUE;
                INSERT INTO {dest}({sqlCols})
                SELECT {sqlCols} FROM {src}"

        runSql conn sql

  p.reports |> List.map syncTable |> ignore

let syncReports (p: Project) =
  use conn = openConn p.dbFile
  syncReportsConn p conn

let getReportValues (p: Project) =
  let reports = p.reports |> List.map (fun { dest = dest } -> dest)

  let queries =
    reports
    |> List.map (fun table ->
      let cols =
        p.source.tables
        |> List.choose (fun t -> if t.name = table then Some t.columns else None)
        |> List.head

      let sel = cols |> sepComma (fun c -> c.name)

      cols, $"SELECT {sel} FROM {table}")

  use conn = openConn p.dbFile

  let reportValues =
    queries
    |> List.map (fun (cols, sql) ->
      let c = conn.CreateCommand()
      c.CommandText <- sql
      let rd = c.ExecuteReader()

      let vss =
        seq {
          while rd.Read() do
            let vs = cols |> List.mapi (fun i _ -> rd.GetString i)
            yield vs

        }
        |> Seq.toList

      cols, vss)

  reportValues

let showReports (p: Project) =

  getReportValues p
  |> List.iter (fun (cols, vss) ->
    let hd = cols |> List.map (fun c -> c.name) |> String.concat "|"
    let sep = List.replicate hd.Length "-" |> String.concat ""

    let rows = vss |> List.map (String.concat "|") |> String.concat "\n"

    printfn $"{hd}"
    printfn $"{sep}"
    printfn $"{rows}")
