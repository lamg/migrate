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

module Migrate.Print

open System
open System.Reflection
open System.IO
open System.Text.RegularExpressions
open Migrate.Types
open SqlParser.Types

let nowStr _ =
  DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffK")

let nowUnix = DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds

let printColor (print: string -> unit) color s =
  let original = Console.ForegroundColor
  Console.ForegroundColor <- color
  print s
  Console.ForegroundColor <- original

let stdPrint x = printfn $"{x}"
let errPrint x = eprintfn $"{x}"
let printGreen = printColor stdPrint ConsoleColor.Green
let printRed = printColor stdPrint ConsoleColor.Red
let printYellow = printColor stdPrint ConsoleColor.Yellow
let printBlue = printColor stdPrint ConsoleColor.Blue

let printDebug = printRed

let printYellowIntro intro text =
  printColor (printf "%s: ") ConsoleColor.Yellow intro
  printfn $"{text}"

let getEnv v =
  v |> Environment.GetEnvironmentVariable |> Option.ofObj

let printResources (asm: Assembly) =
  asm.GetManifestResourceNames() |> Array.iter (printfn "RESOURCE: %s")

let loadFromRes (asm: Assembly) (namespaceForResx: string) (file: string) =
  let namespaceDotFile = $"{namespaceForResx}.{file}"

  try
    use stream = asm.GetManifestResourceStream namespaceDotFile
    use file = new StreamReader(stream)
    (namespaceDotFile, file.ReadToEnd())
  with ex ->
    FailedLoadResFile $"failed loading resource file {namespaceDotFile}: {ex.Message}"
    |> raise

let literalWithEnv =
  function
  | EnvVar { ``member`` = v } ->
    match getEnv v with
    | Some r -> String r
    | None -> ExpectingEnvVar v |> raise
  | x -> x

let printError (e: QueryError) =
  printYellow "running:"
  printfn $"{e.sql}"
  printYellow "got error"
  printRed $"{e.error}"

let joinSql (xs: string list) =
  xs |> String.concat ";\n" |> (fun s -> $"{s};")

let joinSqlPretty =
  List.map (SqlPrettify.SqlPrettify.Pretty >> _.TrimEnd()) >> joinSql

let colorizeSql sql =
  let keywordPattern =
    @"\b(SELECT|FROM|WHERE|GROUP BY|ORDER BY|CREATE|VIEW|TABLE|UNIQUE|PRIMARY KEY|INDEX|AS|WITH|NOT|NULL|AND|OR|LIMIT|OFFSET|ON|LIKE|IN|EXISTS|COALESCE)\b"

  let matchEvaluator (m: Match) =
    let ansiGreen = "\x1b[32m"
    let ansiReset = "\x1b[0m"
    sprintf "%s%s%s" ansiGreen m.Value ansiReset

  Regex.Replace(sql, keywordPattern, matchEvaluator, RegexOptions.IgnoreCase)
