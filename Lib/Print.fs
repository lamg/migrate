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

let nowStr _ =
  DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffK")

let nowUnix () =
  DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds()

let printColor (print: string -> unit) color s =
  let original = Console.ForegroundColor
  Console.ForegroundColor <- color
  print s
  Console.ForegroundColor <- original

let stdPrint x = printfn $"{x}"
let errPrint x = eprintfn $"{x}"

let printError s = printColor errPrint ConsoleColor.Red s

let printGreen s =
  printColor stdPrint ConsoleColor.Green s

let printRed s = printColor stdPrint ConsoleColor.Red s

let printYellow s =
  printColor stdPrint ConsoleColor.Yellow s

let printBlue s = printColor stdPrint ConsoleColor.Blue s

let printDebug s = printRed s

let printYellowIntro intro text =
  printColor (printf "%s: ") ConsoleColor.Yellow intro
  printfn $"{text}"

let getEnv v =
  v |> Environment.GetEnvironmentVariable |> Option.ofObj

let setEnv v value =
  Environment.SetEnvironmentVariable(v, value)
