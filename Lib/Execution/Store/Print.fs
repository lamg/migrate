module internal Migrate.Execution.Store.Print

open Migrate.Types
open Migrate.Print
open Migrate.Execution.Store
open Migrate.Execution.Store.Types

let showMigrations (p: Project) =
  use conn = Migrate.DbUtil.openConn p.dbFile

  Get.getMigrations conn
  |> List.iter (fun x ->
    let m = x.migration
    printYellowIntro "version remarks" m.versionRemarks
    printYellowIntro "schema version" m.schemaVersion
    printYellowIntro "hash" m.hash
    printYellowIntro "date" m.date
    printYellowIntro "database" m.dbFile
    printfn "")

let formatStep (index: int) (sql: string) (error: string option) =
  match error with
  | Some e ->
    printYellow $"statement {index} ❌"
    printRed e
  | None -> printYellow $"statement {index} ✅"

  printfn $"{sql}"

let printLog (m: MigrationLog) =
  printYellowIntro "version remarks" m.migration.versionRemarks
  printYellowIntro "schema version" m.migration.schemaVersion
  printYellowIntro "hash" m.migration.hash
  printYellowIntro "date" m.migration.date
  printfn ""

  m.steps
  |> List.iteri (fun i s ->
    printYellowIntro $"step {i}" $"{s.reason}"
    formatStep i s.sql s.error
    printfn "")

let printShortLog (m: MigrationLog) =
  printYellowIntro "version remarks" m.migration.versionRemarks
  printYellowIntro "schema version" m.migration.schemaVersion
  printYellowIntro "hash" m.migration.hash
  printYellowIntro "date" m.migration.date
  printfn ""

  m.steps
  |> List.iteri (fun i s ->
    let result = if s.error.IsSome then "❌" else "✅"

    printYellowIntro $"{result} step {i}" $"{s.reason}")

let printMigrationIntent (steps: ProposalResult list) =
  steps
  |> List.iteri (fun i step ->
    printGreen $"step {i}"
    printYellowIntro $"reason" $"{step.reason}"

    let sql = step.statements |> Migrate.DbUtil.joinSqlPretty
    formatStep i sql step.error)
