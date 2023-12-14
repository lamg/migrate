module internal Migrate.MigrationPrint

open Types
open MigrationStore
open Print

let showMigrations (p: Project) =
  use conn = DbUtil.openConn p.dbFile

  getMigrations conn
  |> List.iter (fun x ->
    let m = x.migration
    printYellowIntro "version remarks" m.versionRemarks
    printYellowIntro "schema version" m.schemaVersion
    printYellowIntro "hash" m.hash
    printYellowIntro "date" m.date
    printYellowIntro "database" m.dbFile)

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

    let sql = step.statements |> DbUtil.joinSqlPretty
    formatStep i sql step.error)
