module Test.CompareObjects

open System

let printColor color (x: string) =
  let old = Console.ForegroundColor
  Console.ForegroundColor <- color
  Console.Write x
  Console.ForegroundColor <- old

let printRed = printColor ConsoleColor.Red
let printGreen = printColor ConsoleColor.Green

let greenPrefix p x =
  printGreen p
  printfn $" {x}"

let redPrefix p x =
  printRed p
  printfn $" {x}"

let greenFragment x =
  let ansiGreen = "\x1b[32m"
  let ansiReset = "\x1b[0m"
  $"%s{ansiGreen}%s{x}%s{ansiReset}"

let redFragment x =
  let ansiRed = "\x1b[31m"
  let ansiReset = "\x1b[0m"
  $"%s{ansiRed}%s{x}%s{ansiReset}"

let fillArrays (xs: string array) (ys: string array) =
  let diff = xs.Length - ys.Length

  if diff > 0 then
    xs, Array.replicate diff "" |> Array.append ys
  else
    Array.replicate (diff * -1) "" |> Array.append xs, ys

let diffSideBySide (actual: string array) (expected: string array) =
  let max = actual |> Array.maxBy _.Length |> _.Length

  let fillLine (x: string) =
    let diff = max - x.Length
    let fill = if diff > 0 then String.replicate diff " " else ""
    $"{x}{fill}"

  fillArrays actual expected
  |> fun (xs, ys) -> Array.zip xs ys
  |> Array.map (function
    | (x, y) when x <> y -> $"{redFragment (fillLine x)} {greenFragment y}"
    | (x, y) -> $"{fillLine x} {y}")

let diffObjects (actual: 'a) (expected: 'b) =
  let left = ObjectDumper.Dump actual |> _.Split('\n')
  let right = ObjectDumper.Dump expected |> _.Split('\n')
  diffSideBySide left right


let runTest case r x =
  let failed = x <> r
  if failed then
    redPrefix "[FAILED]" case

    diffObjects x r |> Array.iter Console.WriteLine

    printfn ""
  else
    greenPrefix "[OK]" case

  failed |> not


