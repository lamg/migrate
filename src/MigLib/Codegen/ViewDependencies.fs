module internal MigLib.Codegen.ViewDependencies

open System
open System.Collections.Generic
open System.Text

open MigLib.Schema.Types

let private isIdentifierStart c = Char.IsLetter c || c = '_'

let private isIdentifierPart c = Char.IsLetterOrDigit c || c = '_'

let private withoutSqlNoise (sql: string) =
  let builder = StringBuilder sql.Length
  let mutable index = 0

  let appendSpace () = builder.Append ' ' |> ignore

  while index < sql.Length do
    match sql[index] with
    | '\'' ->
      appendSpace ()
      index <- index + 1
      let mutable closedString = false

      while index < sql.Length && not closedString do
        if sql[index] = '\'' then
          if index + 1 < sql.Length && sql[index + 1] = '\'' then
            index <- index + 2
          else
            index <- index + 1
            closedString <- true
        else
          index <- index + 1
    | '-' when index + 1 < sql.Length && sql[index + 1] = '-' ->
      appendSpace ()
      index <- index + 2

      while index < sql.Length && sql[index] <> '\n' do
        index <- index + 1
    | '/' when index + 1 < sql.Length && sql[index + 1] = '*' ->
      appendSpace ()
      index <- index + 2

      while index + 1 < sql.Length && not (sql[index] = '*' && sql[index + 1] = '/') do
        index <- index + 1

      if index + 1 < sql.Length then
        index <- index + 2
    | c ->
      builder.Append c |> ignore
      index <- index + 1

  builder.ToString()

let private collectIdentifiers (sql: string) =
  let cleanSql = withoutSqlNoise sql
  let identifiers = ResizeArray<string>()
  let mutable index = 0

  let readUntil stopChar =
    let start = index + 1
    index <- start

    while index < cleanSql.Length && cleanSql[index] <> stopChar do
      index <- index + 1

    if index < cleanSql.Length then
      let value = cleanSql.Substring(start, index - start)
      index <- index + 1
      value
    else
      cleanSql.Substring start

  while index < cleanSql.Length do
    match cleanSql[index] with
    | '"' -> identifiers.Add(readUntil '"')
    | '`' -> identifiers.Add(readUntil '`')
    | '[' -> identifiers.Add(readUntil ']')
    | c when isIdentifierStart c ->
      let start = index
      index <- index + 1

      while index < cleanSql.Length && isIdentifierPart cleanSql[index] do
        index <- index + 1

      identifiers.Add(cleanSql.Substring(start, index - start))
    | _ -> index <- index + 1

  identifiers |> Seq.toList

let inferDependencies (knownObjectNames: string list) (view: CreateView) =
  let knownNames = HashSet<string>(knownObjectNames, StringComparer.OrdinalIgnoreCase)

  view.sql
  |> collectIdentifiers
  |> List.choose (fun identifier ->
    if
      knownNames.Contains identifier
      && not (String.Equals(identifier, view.name, StringComparison.OrdinalIgnoreCase))
    then
      Some identifier
    else
      None)
  |> List.distinctBy (fun identifier -> identifier.ToLowerInvariant())

let enrichDependencies (tables: CreateTable list) (views: CreateView list) =
  let knownObjectNames = (tables |> List.map _.name) @ (views |> List.map _.name)

  views
  |> List.map (fun view ->
    let dependencies =
      view.dependencies @ inferDependencies knownObjectNames view
      |> List.distinctBy (fun dependency -> dependency.ToLowerInvariant())

    { view with
        dependencies = dependencies })

let sortViews (views: CreateView list) : Result<CreateView list, string> =
  let viewNames =
    HashSet<string>(views |> List.map _.name, StringComparer.OrdinalIgnoreCase)

  let pending = ResizeArray<CreateView>(views)
  let sorted = ResizeArray<CreateView>()
  let createdViews = HashSet<string>(StringComparer.OrdinalIgnoreCase)
  let mutable cycleError = None

  while pending.Count > 0 && cycleError.IsNone do
    let readyIndex =
      pending
      |> Seq.mapi (fun index view -> index, view)
      |> Seq.tryFind (fun (_, view) ->
        view.dependencies
        |> List.filter viewNames.Contains
        |> List.forall createdViews.Contains)

    match readyIndex with
    | Some(index, view) ->
      sorted.Add view
      createdViews.Add view.name |> ignore
      pending.RemoveAt index
    | None ->
      let remaining = pending |> Seq.map _.name |> String.concat ", "
      cycleError <- Some $"View dependency cycle detected among: {remaining}"

  match cycleError with
  | Some error -> Error error
  | None -> Ok(sorted |> Seq.toList)

let orderViews (tables: CreateTable list) (views: CreateView list) : Result<CreateView list, string> =
  views |> enrichDependencies tables |> sortViews
