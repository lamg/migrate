module migrate.ImportGoose.ImportGoose

open System.IO
open FSharpPlus
open FsToolkit.ErrorHandling
open migrate.DeclarativeMigrations
open migrate.DeclarativeMigrations.Types
open migrate.Execution
open migrate.MigrationLog

let internal getUpScript (sql: string) =
  let isNotLine (t: string) (x: string) = x.Contains t |> not

  sql.Split "\n"
  |> Seq.skipWhile (isNotLine "+goose Up")
  |> Seq.drop 1
  |> Seq.takeWhile (isNotLine "+goose Down")
  |> String.concat "\n"


let internal stepsFromGoose (gooseDir: string) =
  try

    let dir = DirectoryInfo gooseDir

    dir.EnumerateFiles()
    |> Seq.choose (fun f ->
      if f.Extension = ".sql" then
        let sql = f.OpenText().ReadToEnd()
        Some(f.Name, sql)
      else
        None)
    |> Seq.sortBy fst
    |> Seq.map (fun (file, sql) -> file, getUpScript sql)
    |> Ok
  with e ->
    Error(MigrationError.ReadFileFailed e.Message)

let scriptFromGoose (withColors, gooseDir) =
  stepsFromGoose gooseDir
  |> Result.map (
    Seq.map (fun (file, sql) -> $"-- {file}\n{FormatSql.format withColors sql}")
    >> String.concat "\n\n"
  )

let execGooseImport (gooseDir: string) =
  let r =
    result {
      let! steps = stepsFromGoose gooseDir
      return! steps |> Seq.map snd |> Seq.toList |> Exec.executeMigration
    }

  if r.IsOk then
    match Exec.getDbSql false with
    | Ok sql -> File.WriteAllText("schema.sql", sql)
    | Error e -> eprintfn $"import error: {e}"

  r

let execGooseImportLog gooseDir =
  let r =
    result {
      let! fileSteps = stepsFromGoose gooseDir

      let createLogTables =
        [ ExecAndLog.migrationLog; ExecAndLog.migrationSteps ]
        |> List.map GenerateSql.Table.createSql

      let steps = fileSteps |> Seq.map snd |> Seq.toList

      return! ExecAndLog.executeMigrations (Some "import Goose migration", createLogTables @ steps)
    }

  if r.IsOk then
    match Exec.getDbSql false with
    | Ok sql -> File.WriteAllText("schema.sql", sql)
    | Error e -> eprintfn $"import error: {e}"

  r
