module internal migrate.DeclarativeMigrations.Migration

open FsToolkit.ErrorHandling
open migrate.DeclarativeMigrations.Solve
open migrate.DeclarativeMigrations.Types
open migrate.DeclarativeMigrations.GenerateSql

let private recreatedTables (columnStatements: string list) =
  columnStatements
  |> List.choose (fun stmt ->
    let m =
      System.Text.RegularExpressions.Regex.Match(
        stmt,
        @"^ALTER TABLE (\w+)_temp RENAME TO (\w+)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
      )

    if m.Success && m.Groups.[1].Value = m.Groups.[2].Value then
      Some m.Groups.[2].Value
    else
      None)
  |> Set.ofList

let private viewsDependingOn (tables: Set<string>) (views: CreateView list) =
  views
  |> List.filter (fun v -> v.dependencies |> List.exists (fun dep -> tables |> Set.contains dep))
  |> List.map _.name
  |> Set.ofList

let private filterOutViews (namesToExclude: Set<string>) (fileSorted: FileSorted) =
  { fileSorted with
      file =
        { fileSorted.file with
            views =
              fileSorted.file.views
              |> List.filter (fun v -> namesToExclude |> Set.contains v.name |> not) } }

let private viewDropsByOrder (fileSorted: FileSorted) (viewNames: Set<string>) =
  fileSorted.sortedRelations
  |> List.rev
  |> List.filter (fun name -> viewNames |> Set.contains name)
  |> List.map View.dropSql

let private viewCreatesByOrder (fileSorted: FileSorted) (viewNames: Set<string>) =
  fileSorted.sortedRelations
  |> List.filter (fun name -> viewNames |> Set.contains name)
  |> List.map (fun name -> fileSorted.file.views |> List.find (fun v -> v.name = name) |> View.createSql)

let private splitPragmas (statements: string list) =
  let isPragma (s: string) = s.StartsWith "PRAGMA"

  let leadingPragmas = statements |> List.takeWhile isPragma
  let remaining = statements |> List.skip leadingPragmas.Length
  let trailingPragmas = remaining |> List.rev |> List.takeWhile isPragma |> List.rev
  let body = remaining |> List.take (remaining.Length - trailingPragmas.Length)

  leadingPragmas, body, trailingPragmas

let migration (dbSchema: SqlFile, expectedSchema: SqlFile) =
  result {
    let dbSorted, dbMissing = sortFile dbSchema
    let expectedSorted, expectedMissing = sortFile expectedSchema

    do!
      match dbMissing, expectedMissing with
      | [], [] -> Ok()
      | _ -> Error(MissingDependencies(dbMissing, expectedMissing))

    let columnMs = columnMigrations dbSchema.tables expectedSchema.tables
    let columnLeadingPragmas, columnBody, columnTrailingPragmas = splitPragmas columnMs
    let recreated = recreatedTables columnMs

    let dbViewsAffected = viewsDependingOn recreated dbSchema.views
    let expectedViewsAffected = viewsDependingOn recreated expectedSchema.views
    let affectedViews = Set.union dbViewsAffected expectedViewsAffected

    let preColumnViewDrops = viewDropsByOrder dbSorted dbViewsAffected
    let postTableViewCreates = viewCreatesByOrder expectedSorted expectedViewsAffected

    let dbSortedRemainingViews = filterOutViews affectedViews dbSorted
    let expectedSortedRemainingViews = filterOutViews affectedViews expectedSorted

    let tableMs = tableMigrationsSql dbSorted expectedSorted
    let viewMs = viewMigrationsSql dbSortedRemainingViews expectedSortedRemainingViews
    let indexMs = indexMigrationsSql dbSorted expectedSorted
    let triggerMs = triggerMigrationSql dbSorted expectedSorted

    let sql =
      columnLeadingPragmas
      @ preColumnViewDrops
      @ columnBody
      @ tableMs
      @ viewMs
      @ postTableViewCreates
      @ indexMs
      @ triggerMs
      @ columnTrailingPragmas

    return sql
  }
