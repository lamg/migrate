module internal migrate.DeclarativeMigrations.Solve

open System.Collections.Generic

open FsToolkit.ErrorHandling

open Types
open GenerateSql

type Graph<'a when 'a: equality>() =
  let mutable graph = Dictionary<'a, HashSet<'a>>()

  member _.addVertex(x: 'a) = graph[x] <- HashSet<'a>()

  member _.addEdge(u: 'a, v: 'a) =
    if not (graph.ContainsKey(u)) then
      graph[u] <- HashSet<'a>()

    graph[u].Add(v) |> ignore

  member _.topologicalSort() =
    let visited = HashSet<'a>()
    let stack = Stack<'a>()

    let rec visit node =
      if not (visited.Contains(node)) then
        visited.Add(node) |> ignore

        if graph.ContainsKey(node) then
          for neighbor in graph[node] do
            visit neighbor

        stack.Push(node)

    for key in graph.Keys do
      visit key

    stack.ToArray() |> Array.toList

  member _.hasEdge(x: 'a, y: 'a) =
    if graph.ContainsKey x then graph[x].Contains y else false

  member _.isDependency(x: 'a) = graph.ContainsKey x

let findOne f xs = xs |> List.filter f |> List.head

let splitResult (rs: Result<'a, 'b> list) =
  let getOk =
    function
    | Ok x -> x
    | Error e -> failwith $"Expecting Ok, got {e}"

  let getErr =
    function
    | Error e -> e
    | Ok x -> failwith $"Expecting Error, got {x}"

  let oks, errs = rs |> List.partition Result.isOk
  oks |> List.map getOk, errs |> List.map getErr

let dependentRelations (file: SqlFile) =
  let dependentTables (t: CreateTable) =
    let xs =
      t.constraints
      |> List.choose (function
        | ForeignKey f -> Some(f.refTable, t.name)
        | _ -> None)

    let missingRefs =
      xs
      |> List.map fst
      |> List.filter (fun r -> file.tables |> List.exists (fun n -> n.name = r) |> not)

    xs, missingRefs

  let dependencies (element: string) (relations: string list) =
    relations
    |> List.map (fun r ->
      let x = file.tables |> Seq.tryFind (fun t -> t.name = r) |> Option.map _.name

      let y = file.views |> Seq.tryFind (fun v -> v.name = r) |> Option.map _.name

      match x, y with
      | Some a, _ -> Ok a
      | _, Some b -> Ok b
      | _ -> Error r)
    |> splitResult
    |> fun (xs, ys) -> (xs |> List.map (fun x -> element, x)), ys

  let viewDependencies (v: CreateView) = v.dependencies |> dependencies v.name

  let triggerDependencies (t: CreateTrigger) = t.dependencies |> dependencies t.name

  let indexDependencies (i: CreateIndex) =
    let missing =
      if file.tables |> List.exists (fun t -> t.name = i.table) then
        []
      else
        [ i.table ]

    [ (i.name, i.table) ], missing

  let unzipConcat = List.unzip >> fun (a, b) -> List.concat a, List.concat b
  let tables, missingT = file.tables |> List.map dependentTables |> unzipConcat
  let rs, missingR = file.views |> List.map viewDependencies |> unzipConcat

  let indexedTables, missingIT =
    file.indexes |> List.map indexDependencies |> unzipConcat

  let triggerTables, missingTT =
    file.triggers |> List.map triggerDependencies |> unzipConcat

  let graph = Graph<string>()
  file.tables |> List.iter (_.name >> graph.addVertex)
  file.views |> List.iter (_.name >> graph.addVertex)
  file.indexes |> List.iter (_.name >> graph.addVertex)
  tables @ rs @ indexedTables @ triggerTables |> List.iter graph.addEdge
  graph, missingT @ missingR @ missingIT @ missingTT

let tableDifferences (left: SqlFile) (right: SqlFile) =
  // appears on left and not on right -> table removed
  // appears on right and not on left -> table added

  let tableToSet = List.map (fun (t: CreateTable) -> t.name) >> Set.ofList
  let leftSet, rightSet = tableToSet left.tables, tableToSet right.tables

  let diff a b = a - b |> Set.toList
  let removes, adds = diff leftSet rightSet, diff rightSet leftSet

  // two tables with different names in left and right but same schema -> table renamed
  let schemaToName =
    List.map (fun (t: CreateTable) -> (t.columns, t.constraints), t.name)
    >> Map.ofList

  let leftSchemaNames, rightSchemaNames =
    schemaToName left.tables, schemaToName right.tables

  let commonSchemas =
    Set.intersect (Set.ofSeq leftSchemaNames.Keys) (Set.ofSeq rightSchemaNames.Keys)
    |> Set.toList

  let containsDefinition (file: SqlFile) (name: string) =
    file.tables |> List.exists (fun t -> t.name = name)
    || file.views |> List.exists (fun v -> v.name = name)
    || file.indexes |> List.exists (fun i -> i.name = name)

  let renames =
    commonSchemas
    |> List.choose (fun schema ->
      let oldName, newName = leftSchemaNames[schema], rightSchemaNames[schema]
      let nameRemained = oldName = newName

      let oldTableRemained = containsDefinition right oldName
      let alreadyExists = containsDefinition left newName

      if nameRemained || oldTableRemained || alreadyExists then
        None
      else
        Some(oldName, newName))

  let notIn xs ys =
    ys |> List.filter (fun y -> xs |> List.contains y |> not)

  {| adds = adds |> notIn (renames |> List.map snd)
     removes = removes |> notIn (renames |> List.map fst)
     renames = renames |}

let isMemberOf xs x = List.contains x xs

type FileSorted =
  { file: SqlFile
    sortedRelations: string list }

let tableMigrations (left: FileSorted) (right: FileSorted) =
  let diffs = tableDifferences left.file right.file

  // drops have to be executed from less dependent to more dependent
  let drops =
    left.sortedRelations |> List.rev |> List.filter (isMemberOf diffs.removes)

  // creates have to be executed from more dependent to less dependent,
  // and we need the full table definition for all of them
  let creates =
    right.sortedRelations
    |> List.filter (isMemberOf diffs.adds)
    |> List.map (fun x -> right.file.tables |> findOne (fun t -> t.name = x))

  {| drops = drops
     creates = creates
     renames = diffs.renames |}

let sortFile (file: SqlFile) =
  let graph, missing = dependentRelations file

  { file = file
    sortedRelations = graph.topologicalSort () },
  missing

let tableMigrationsSql (left: FileSorted) (right: FileSorted) =
  let ms = tableMigrations left right

  let creates = ms.creates |> List.map Table.createSql
  let drops = ms.drops |> List.map Table.dropSql
  let renames = ms.renames |> List.map Table.sqlRenameTable
  creates @ drops @ renames

type SetSortSql =
  { set: Set<string>
    sql: string -> string
    sorted: string list }

let simpleMigrationSql (left: SetSortSql) (right: SetSortSql) =
  let removes, adds =
    let diff a b = a - b |> Set.toList
    diff left.set right.set, diff right.set left.set

  let drops =
    left.sorted |> List.rev |> List.filter (isMemberOf removes) |> List.map left.sql

  let creates = right.sorted |> List.filter (isMemberOf adds) |> List.map right.sql
  drops @ creates

let viewMigrationsSql (left: FileSorted) (right: FileSorted) =
  let viewToSet = List.map (fun (v: CreateView) -> v.name) >> Set.ofList

  let ls =
    { set = viewToSet left.file.views
      sql = View.dropSql
      sorted = left.sortedRelations }

  let rs =
    { set = viewToSet right.file.views
      sql = fun v -> right.file.views |> findOne (fun n -> n.name = v) |> View.createSql
      sorted = right.sortedRelations }

  simpleMigrationSql ls rs

let indexMigrationsSql (left: FileSorted) (right: FileSorted) =
  let indexToSet = List.map Index.createSql >> Set.ofList

  let ls =
    { set = indexToSet left.file.indexes
      sql = Index.dropSql
      sorted = left.sortedRelations }

  let rs =
    { set = indexToSet right.file.indexes
      sql = fun i -> right.file.indexes |> findOne (fun n -> n.name = i) |> Index.createSql
      sorted = right.sortedRelations }

  simpleMigrationSql ls rs

let triggerMigrationSql (left: FileSorted) (right: FileSorted) =
  let triggerToSet = List.map (fun (t: CreateTrigger) -> t.name) >> Set.ofList

  let ls =
    { set = triggerToSet left.file.triggers
      sql = Trigger.dropSql
      sorted = left.sortedRelations }

  let rs =
    { set = triggerToSet right.file.triggers
      sql = (fun v -> right.file.triggers |> findOne (fun n -> n.name = v) |> Trigger.createSql)
      sorted = right.sortedRelations }

  simpleMigrationSql ls rs

let columnMigrations (left: CreateTable list) (right: CreateTable list) =
  let migrateMatching (left: CreateTable, right: CreateTable) =
    let colToSet = List.map (fun (c: ColumnDef) -> c.name, c.columnType) >> Set.ofList
    let leftSet = left.columns |> colToSet
    let rightSet = right.columns |> colToSet

    let removes = leftSet - rightSet |> Set.toList |> List.map fst
    let adds = rightSet - leftSet |> Set.toList |> List.map fst

    let emptyIntersection (xs: 'a list) (ys: 'a list) =
      Set.intersect (Set.ofList xs) (Set.ofList ys) |> Seq.toList |> List.isEmpty


    match adds, removes with
    | [], _ ->

      let recreate =
        left.constraints
        |> List.exists (function
          | ForeignKey fk -> fk.columns |> emptyIntersection removes |> not
          | _ -> false)

      if recreate then
        let colTuple = right.columns |> List.map _.name |> String.concat ", "
        let tempName = $"{right.name}_temp"

        [ Table.createSql { right with name = tempName }
          $"INSERT INTO {tempName}({colTuple}) SELECT {colTuple} FROM {left.name}"
          Table.dropSql left.name
          Table.sqlRenameTable (tempName, right.name) ]
      else
        removes |> List.map (Column.dropSql left.name)
    | _ ->
      let addedColumns =
        adds
        |> List.map (fun name -> right.columns |> List.find (fun c -> c.name = name) |> Table.columnDefSql)

      [ $"-- WARNING addition of columns {addedColumns} requires a complimentary script to ensure data integrity"
        Table.sqlRenameTable (left.name, $"{left.name}_old")
        Table.createSql right ]

  let matchTables =
    left
    |> List.choose (fun l -> right |> List.tryFind (fun r -> l.name = r.name) |> Option.map (fun r -> l, r))

  matchTables |> List.map migrateMatching |> List.concat
