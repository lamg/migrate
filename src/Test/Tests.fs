module Tests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open MigLib.Db
open MigLib.CodeGen.CodeGen
open MigLib.DeclarativeMigrations.Types
open MigLib.DeclarativeMigrations.DataCopy
open MigLib.DeclarativeMigrations.DrainReplay
open MigLib.DeclarativeMigrations.SchemaDiff
open MigLib.HotMigration
open MigLib.SchemaReflection
open MigLib.SchemaScript
open Microsoft.Data.Sqlite
open Xunit

let private mkColumn name columnType constraints =
  { name = name
    columnType = columnType
    constraints = constraints }

let private mkTable name columns constraints =
  { name = name
    columns = columns
    constraints = constraints
    queryByAnnotations = []
    queryLikeAnnotations = []
    queryByOrCreateAnnotations = []
    insertOrIgnoreAnnotations = [] }

let private mkForeignKey refTable refColumns =
  ForeignKey
    { columns = []
      refTable = refTable
      refColumns = refColumns
      onDelete = None
      onUpdate = None }

let private mkRow pairs = pairs |> Map.ofList

let private mkLogEntry id txnId ordering operation sourceTable rowData =
  { id = id
    txnId = txnId
    ordering = ordering
    operation = operation
    sourceTable = sourceTable
    rowData = rowData }

let private cliIoLock = obj ()

let private runMigCliInDirectory (workingDirectory: string option) (args: string list) =
  lock cliIoLock (fun () ->
    let originalOut = Console.Out
    let originalErr = Console.Error
    let originalDirectory = Directory.GetCurrentDirectory()
    use outWriter = new StringWriter()
    use errWriter = new StringWriter()
    Console.SetOut outWriter
    Console.SetError errWriter

    try
      match workingDirectory with
      | Some dir -> Directory.SetCurrentDirectory dir
      | None -> ()

      let exitCode = Mig.Program.main (args |> List.toArray)
      exitCode, outWriter.ToString(), errWriter.ToString()
    finally
      Directory.SetCurrentDirectory originalDirectory
      Console.SetOut originalOut
      Console.SetError originalErr)

let private runMigCli (args: string list) = runMigCliInDirectory None args

let private deriveShortSchemaHashFromScript (schemaPath: string) =
  let normalizeLineEndings (text: string) =
    text.Replace("\r\n", "\n").Replace("\r", "\n")

  let normalizedSchema = File.ReadAllText schemaPath |> normalizeLineEndings
  use sha256 = SHA256.Create()
  let schemaBytes = Encoding.UTF8.GetBytes normalizedSchema
  let hashBytes = sha256.ComputeHash schemaBytes
  Convert.ToHexString(hashBytes).ToLowerInvariant().Substring(0, 16)

let private deriveDeterministicNewDbPathFromSchema (directoryPath: string) (schemaPath: string) =
  let schemaHash = deriveShortSchemaHashFromScript schemaPath
  let directoryName = DirectoryInfo(directoryPath).Name
  Path.Combine(directoryPath, $"{directoryName}-{schemaHash}.sqlite")

let private findGitRootOrFail (startDirectory: string) =
  let rec loop (currentDirectory: string) =
    if Directory.Exists(Path.Combine(currentDirectory, ".git")) then
      Some currentDirectory
    else
      let parent = Directory.GetParent currentDirectory

      if isNull parent then None else loop parent.FullName

  match loop startDirectory with
  | Some gitRoot -> gitRoot
  | None -> failwith $"Could not locate git repository root from: {startDirectory}"

let private gitHeadCommitOrFail (repositoryDirectory: string) =
  let startInfo = ProcessStartInfo()
  startInfo.FileName <- "git"
  startInfo.UseShellExecute <- false
  startInfo.RedirectStandardOutput <- true
  startInfo.RedirectStandardError <- true
  startInfo.CreateNoWindow <- true
  startInfo.ArgumentList.Add "-C"
  startInfo.ArgumentList.Add repositoryDirectory
  startInfo.ArgumentList.Add "rev-parse"
  startInfo.ArgumentList.Add "HEAD"

  use proc = Process.Start startInfo

  if isNull proc then
    failwith "Could not start git process to resolve HEAD commit."
  else
    let output = proc.StandardOutput.ReadToEnd()
    let errorOutput = proc.StandardError.ReadToEnd()
    let exited = proc.WaitForExit 2000

    if not exited then
      failwith "git rev-parse HEAD timed out."
    elif proc.ExitCode <> 0 then
      failwith $"git rev-parse HEAD failed: {errorOutput}"
    else
      let commit = output.Trim()

      if String.IsNullOrWhiteSpace commit then
        failwith "git rev-parse HEAD returned an empty commit hash."
      else
        commit

let private assertCliHelpOutput (args: string list) (expectedUsage: string) (expectedFragments: string list) =
  let exitCode, stdOut, stdErr = runMigCli args
  Assert.Equal(1, exitCode)
  Assert.Contains(expectedUsage, stdOut)

  expectedFragments
  |> List.iter (fun fragment -> Assert.Contains(fragment, stdOut))

  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")

[<AutoIncPK "id">]
[<Unique "name">]
[<Index "name">]
[<SelectBy("name", "age")>]
[<SelectLike "name">]
[<SelectByOrInsert("name", "age")>]
[<InsertOrIgnore>]
type ReflectionStudent = { id: int64; name: string; age: int64 }

[<AutoIncPK "id">]
type ReflectionUser = { id: int64; name: string }

[<AutoIncPK "id">]
[<OnDeleteCascade "user">]
type ReflectionUserWallet =
  { id: int64
    user: ReflectionUser
    address: string }

type ReflectionStudentOpt = WithEmail of ReflectionStudent * email: string

[<ViewSql "SELECT id, name FROM reflection_student">]
[<SelectBy "name">]
type ReflectionStudentView = { id: int64; name: string }

[<AutoIncPK "id">]
type JoinStudent = { id: int64; name: string; age: int64 }

[<AutoIncPK "id">]
type JoinCourse =
  { id: int64
    title: string
    student: JoinStudent }

type JoinCourseGrade = { course: JoinCourse; grade: float }

[<PK "slug">]
[<SelectByOrInsert "slug">]
type SlugArticle = { slug: string; title: string }

[<PK "code">]
[<SelectByOrInsert "name">]
type Product = { code: string; name: string }

type ProductOpt = WithStock of Product * stock: int64

[<AutoIncPK "id">]
[<PK "id">]
type ConflictingPkStudent = { id: int64; name: string }

type ExternalAccount = { id: int64; name: string }

[<AutoIncPK "id">]
type WalletWithOutsideRef = { id: int64; account: ExternalAccount }

type ParentWithoutPk = { name: string }

[<AutoIncPK "id">]
type ChildWithParentWithoutPk = { id: int64; parent: ParentWithoutPk }

[<AutoIncPK "id">]
type JoinChainA = { id: int64; name: string }

[<AutoIncPK "id">]
type JoinChainB = { id: int64; chainA: JoinChainA }

[<AutoIncPK "id">]
type JoinChainC = { id: int64; label: string }

[<AutoIncPK "id">]
type JoinChainD = { id: int64; chainC: JoinChainC }

[<View>]
[<Join(typeof<JoinChainA>, typeof<JoinChainB>)>]
[<Join(typeof<JoinChainC>, typeof<JoinChainD>)>]
type DisconnectedJoinView = { id: int64 }

[<View>]
[<Join(typeof<JoinCourse>, typeof<JoinStudent>)>]
[<Join(typeof<JoinCourseGrade>, typeof<JoinCourse>)>]
type JoinStudentCourseGrade =
  { studentId: int64
    studentName: string
    title: string
    grade: float }

[<Fact>]
let ``schema reflection maps records, foreign keys, and query annotations`` () =
  let types =
    [ typeof<ReflectionStudent>
      typeof<ReflectionUser>
      typeof<ReflectionUserWallet> ]

  match buildSchemaFromTypes types with
  | Error e -> failwith $"reflection failed: {e}"
  | Ok schema ->
    let student =
      schema.tables |> List.find (fun table -> table.name = "reflection_student")

    let idColumn = student.columns |> List.find (fun column -> column.name = "id")

    let hasAutoPk =
      idColumn.constraints
      |> List.exists (function
        | PrimaryKey pk -> pk.isAutoincrement
        | _ -> false)

    Assert.True hasAutoPk

    Assert.True(
      student.queryByAnnotations
      |> List.exists (fun q -> q.columns = [ "name"; "age" ])
    )

    Assert.True(student.queryLikeAnnotations |> List.exists (fun q -> q.columns = [ "name" ]))

    Assert.True(
      student.queryByOrCreateAnnotations
      |> List.exists (fun q -> q.columns = [ "name"; "age" ])
    )

    Assert.True(not student.insertOrIgnoreAnnotations.IsEmpty)

    Assert.True(
      schema.indexes
      |> List.exists (fun index -> index.name = "ix_reflection_student_name")
    )

    let wallet =
      schema.tables |> List.find (fun table -> table.name = "reflection_user_wallet")

    let userFkColumn =
      wallet.columns |> List.find (fun column -> column.name = "user_id")

    let fkConstraint =
      userFkColumn.constraints
      |> List.tryPick (function
        | ForeignKey fk -> Some fk
        | _ -> None)

    match fkConstraint with
    | None -> failwith "Expected foreign key on reflection_user_wallet.user_id"
    | Some fk ->
      Assert.Equal("reflection_user", fk.refTable)
      Assert.Equal<string>("id", fk.refColumns.Head)

      match fk.onDelete with
      | Some Cascade -> ()
      | _ -> failwith "Expected ON DELETE CASCADE on reflection_user_wallet.user_id"

[<Fact>]
let ``schema diff detects renamed tables when schema matches`` () =
  let sourceSchema =
    { emptyFile with
        tables =
          [ mkTable
              "legacy_student"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ] ]
              [] ] }

  let targetSchema =
    { emptyFile with
        tables =
          [ mkTable
              "student"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ] ]
              [] ] }

  let diff = diffSchemas sourceSchema targetSchema

  Assert.Empty diff.addedTables
  Assert.Empty diff.removedTables
  Assert.Equal<(string * string) list>([ "legacy_student", "student" ], diff.renamedTables)
  Assert.Equal<(string * string) list>([ "legacy_student", "student" ], diff.matchedTables)

[<Fact>]
let ``table copy mapping infers renamed and added columns`` () =
  let sourceTable =
    mkTable
      "student"
      [ mkColumn
          "id"
          SqlInteger
          [ PrimaryKey
              { constraintName = None
                columns = []
                isAutoincrement = true } ]
        mkColumn "full_name" SqlText [ NotNull ]
        mkColumn "age" SqlInteger [ NotNull ]
        mkColumn "legacy_note" SqlText [ NotNull ] ]
      []

  let targetTable =
    mkTable
      "student"
      [ mkColumn
          "id"
          SqlInteger
          [ PrimaryKey
              { constraintName = None
                columns = []
                isAutoincrement = true } ]
        mkColumn "name" SqlText [ NotNull ]
        mkColumn "age" SqlInteger [ NotNull ]
        mkColumn "status" SqlText [ NotNull; Default(String "active") ]
        mkColumn "score" SqlReal [ NotNull ] ]
      []

  let mapping = buildTableCopyMapping sourceTable targetTable

  let byTarget =
    mapping.columnMappings
    |> List.map (fun entry -> entry.targetColumn, entry.source)
    |> Map.ofList

  match byTarget["id"] with
  | SourceColumn "id" -> ()
  | other -> failwith $"Expected id to map from source id, got {other}"

  match byTarget["name"] with
  | SourceColumn "full_name" -> ()
  | other -> failwith $"Expected name to map from full_name, got {other}"

  match byTarget["status"] with
  | DefaultExpr(String "active") -> ()
  | other -> failwith $"Expected status default to be 'active', got {other}"

  match byTarget["score"] with
  | TypeDefault SqlReal -> ()
  | other -> failwith $"Expected score to use SqlReal type default, got {other}"

  Assert.Contains(("full_name", "name"), mapping.renamedColumns)
  Assert.Contains("status", mapping.addedTargetColumns)
  Assert.Contains("score", mapping.addedTargetColumns)
  Assert.Contains("legacy_note", mapping.droppedSourceColumns)

[<Fact>]
let ``schema copy plan keeps renamed table mappings`` () =
  let sourceSchema =
    { emptyFile with
        tables =
          [ mkTable
              "legacy_user"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "full_name" SqlText [ NotNull ] ]
              []
            mkTable
              "audit_log"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "message" SqlText [ NotNull ] ]
              [] ] }

  let targetSchema =
    { emptyFile with
        tables =
          [ mkTable
              "user"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ]
                mkColumn "status" SqlText [ NotNull; Default(String "active") ] ]
              []
            mkTable
              "audit_log"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "message" SqlText [ NotNull ] ]
              [] ] }

  let plan = buildSchemaCopyPlan sourceSchema targetSchema

  Assert.Equal<(string * string) list>([ "legacy_user", "user" ], plan.diff.renamedTables)

  let userTableMapping =
    plan.tableMappings |> List.find (fun mapping -> mapping.targetTable = "user")

  Assert.Equal("legacy_user", userTableMapping.sourceTable)
  Assert.Contains(("full_name", "name"), userTableMapping.renamedColumns)

[<Fact>]
let ``taskTxn records writes into migration log when marker is recording`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_tasktxn_recording_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dbPath = Path.Combine(tempDir, "recording.db")

  use setupConn = new SqliteConnection($"Data Source={dbPath}")
  setupConn.Open()

  [ "CREATE TABLE student(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);"
    "CREATE TABLE _migration_marker(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_marker(id, status) VALUES (0, 'recording');"
    "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupConn)
    cmd.ExecuteNonQuery() |> ignore)

  setupConn.Close()

  let result =
    taskTxn dbPath {
      let! newId =
        fun tx ->
          task {
            use cmd =
              new SqliteCommand("INSERT INTO student(name) VALUES (@name)", tx.Connection, tx)

            cmd.Parameters.AddWithValue("@name", "Alice") |> ignore
            MigrationLog.ensureWriteAllowed tx
            let! _ = cmd.ExecuteNonQueryAsync()
            use idCmd = new SqliteCommand("SELECT last_insert_rowid()", tx.Connection, tx)
            let! idObj = idCmd.ExecuteScalarAsync()
            let id = idObj |> unbox<int64>
            MigrationLog.recordInsert tx "student" [ "id", box id; "name", box "Alice" ]
            return Ok id
          }

      return newId
    }
    |> fun t -> t.Result

  match result with
  | Error ex -> failwith $"Expected successful transaction, got error: {ex.Message}"
  | Ok id -> Assert.Equal(1L, id)

  use verifyConn = new SqliteConnection($"Data Source={dbPath}")
  verifyConn.Open()

  use logCmd =
    new SqliteCommand(
      "SELECT txn_id, ordering, operation, table_name, row_data FROM _migration_log ORDER BY id",
      verifyConn
    )

  use reader = logCmd.ExecuteReader()
  Assert.True(reader.Read())
  let txnId = reader.GetInt64(0)
  let ordering = reader.GetInt64(1)
  let operation = reader.GetString(2)
  let tableName = reader.GetString(3)
  let rowData = reader.GetString(4)

  Assert.True(txnId > 0L)
  Assert.Equal(1L, ordering)
  Assert.Equal("insert", operation)
  Assert.Equal("student", tableName)

  let json = JsonNode.Parse(rowData).AsObject()
  Assert.Equal("Alice", json["name"].GetValue<string>())
  Assert.Equal(1L, json["id"].GetValue<int64>())
  Assert.False(reader.Read())

  verifyConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``taskTxn rejects writes when marker is draining`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_tasktxn_draining_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dbPath = Path.Combine(tempDir, "draining.db")

  use setupConn = new SqliteConnection($"Data Source={dbPath}")
  setupConn.Open()

  [ "CREATE TABLE student(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);"
    "CREATE TABLE _migration_marker(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_marker(id, status) VALUES (0, 'draining');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupConn)
    cmd.ExecuteNonQuery() |> ignore)

  setupConn.Close()

  let result =
    taskTxn dbPath {
      let! _ =
        fun tx ->
          task {
            MigrationLog.ensureWriteAllowed tx

            use cmd =
              new SqliteCommand("INSERT INTO student(name) VALUES ('Bob')", tx.Connection, tx)

            let! _ = cmd.ExecuteNonQueryAsync()
            return Ok()
          }

      return ()
    }
    |> fun t -> t.Result

  match result with
  | Ok _ -> failwith "Expected draining mode to reject writes"
  | Error ex -> Assert.Contains("drain", ex.Message.ToLowerInvariant())

  use verifyConn = new SqliteConnection($"Data Source={dbPath}")
  verifyConn.Open()
  use countCmd = new SqliteCommand("SELECT COUNT(*) FROM student", verifyConn)
  let count = countCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(0L, count)

  verifyConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``taskTxn does not record writes when marker is absent`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_tasktxn_nomarker_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dbPath = Path.Combine(tempDir, "nomarker.db")

  use setupConn = new SqliteConnection($"Data Source={dbPath}")
  setupConn.Open()

  [ "CREATE TABLE student(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);"
    "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupConn)
    cmd.ExecuteNonQuery() |> ignore)

  setupConn.Close()

  let result =
    taskTxn dbPath {
      let! _ =
        fun tx ->
          task {
            use cmd =
              new SqliteCommand("INSERT INTO student(name) VALUES ('Carol')", tx.Connection, tx)

            MigrationLog.ensureWriteAllowed tx
            let! _ = cmd.ExecuteNonQueryAsync()
            MigrationLog.recordInsert tx "student" [ "name", box "Carol" ]
            return Ok()
          }

      return ()
    }
    |> fun t -> t.Result

  match result with
  | Error ex -> failwith $"Expected success without marker, got {ex.Message}"
  | Ok() -> ()

  use verifyConn = new SqliteConnection($"Data Source={dbPath}")
  verifyConn.Open()
  use countCmd = new SqliteCommand("SELECT COUNT(*) FROM _migration_log", verifyConn)
  let count = countCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(0L, count)

  verifyConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``bulk copy plan orders parent tables before FK dependents`` () =
  let sourceSchema =
    { emptyFile with
        tables =
          [ mkTable
              "legacy_account"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ] ]
              []
            mkTable
              "invoice"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "legacy_account_id" SqlInteger [ NotNull; mkForeignKey "legacy_account" [ "id" ] ]
                mkColumn "total" SqlReal [ NotNull ] ]
              [] ] }

  let targetSchema =
    { emptyFile with
        tables =
          [ mkTable
              "account"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ] ]
              []
            mkTable
              "invoice"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "account_id" SqlInteger [ NotNull; mkForeignKey "account" [ "id" ] ]
                mkColumn "total" SqlReal [ NotNull ] ]
              [] ] }

  match buildBulkCopyPlan sourceSchema targetSchema with
  | Error error -> failwith $"bulk copy plan failed: {error}"
  | Ok plan ->
    let orderedTargets = plan.steps |> List.map (fun step -> step.mapping.targetTable)

    Assert.Equal<string list>([ "account"; "invoice" ], orderedTargets)

    let accountStep =
      plan.steps |> List.find (fun step -> step.mapping.targetTable = "account")

    let invoiceStep =
      plan.steps |> List.find (fun step -> step.mapping.targetTable = "invoice")

    Assert.Equal<string list>([ "name" ], accountStep.insertColumns)
    Assert.Equal<string list>([ "account_id"; "total" ], invoiceStep.insertColumns)

[<Fact>]
let ``bulk copy row projection translates FK values via ID mapping`` () =
  let sourceSchema =
    { emptyFile with
        tables =
          [ mkTable
              "legacy_account"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ] ]
              []
            mkTable
              "invoice"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "legacy_account_id" SqlInteger [ NotNull; mkForeignKey "legacy_account" [ "id" ] ]
                mkColumn "total" SqlReal [ NotNull ] ]
              [] ] }

  let targetSchema =
    { emptyFile with
        tables =
          [ mkTable
              "account"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ] ]
              []
            mkTable
              "invoice"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "account_id" SqlInteger [ NotNull; mkForeignKey "account" [ "id" ] ]
                mkColumn "total" SqlReal [ NotNull ] ]
              [] ] }

  match buildBulkCopyPlan sourceSchema targetSchema with
  | Error error -> failwith $"bulk copy plan failed: {error}"
  | Ok plan ->
    let accountStep =
      plan.steps |> List.find (fun step -> step.mapping.targetTable = "account")

    let invoiceStep =
      plan.steps |> List.find (fun step -> step.mapping.targetTable = "invoice")

    let sourceAccountRow = mkRow [ "id", Integer 10; "name", String "Alice" ]

    let sourceInvoiceRow =
      mkRow [ "id", Integer 1; "legacy_account_id", Integer 10; "total", Real 42.5 ]

    let accountTargetRow, _, accountInsertValues =
      match projectRowForInsert accountStep sourceAccountRow emptyIdMappings with
      | Ok result -> result
      | Error error -> failwith $"account projection failed: {error}"

    Assert.Equal<Expr list>([ String "Alice" ], accountInsertValues)

    let idMappingsAfterAccount =
      match recordIdMapping accountStep sourceAccountRow accountTargetRow (Some [ Integer 100 ]) emptyIdMappings with
      | Ok mappings -> mappings
      | Error error -> failwith $"record ID mapping failed: {error}"

    let invoiceTargetRow, _, invoiceInsertValues =
      match projectRowForInsert invoiceStep sourceInvoiceRow idMappingsAfterAccount with
      | Ok result -> result
      | Error error -> failwith $"invoice projection failed: {error}"

    Assert.Equal<Expr list>([ Integer 100; Real 42.5 ], invoiceInsertValues)

    match invoiceTargetRow.TryFind "account_id" with
    | Some(Integer 100) -> ()
    | other -> failwith $"Expected translated account_id=100, got {other}"

[<Fact>]
let ``bulk copy row projection fails when FK mapping is missing`` () =
  let sourceSchema =
    { emptyFile with
        tables =
          [ mkTable
              "legacy_account"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ] ]
              []
            mkTable
              "invoice"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "legacy_account_id" SqlInteger [ NotNull; mkForeignKey "legacy_account" [ "id" ] ]
                mkColumn "total" SqlReal [ NotNull ] ]
              [] ] }

  let targetSchema =
    { emptyFile with
        tables =
          [ mkTable
              "account"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ] ]
              []
            mkTable
              "invoice"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "account_id" SqlInteger [ NotNull; mkForeignKey "account" [ "id" ] ]
                mkColumn "total" SqlReal [ NotNull ] ]
              [] ] }

  match buildBulkCopyPlan sourceSchema targetSchema with
  | Error error -> failwith $"bulk copy plan failed: {error}"
  | Ok plan ->
    let invoiceStep =
      plan.steps |> List.find (fun step -> step.mapping.targetTable = "invoice")

    let sourceInvoiceRow =
      mkRow [ "id", Integer 1; "legacy_account_id", Integer 10; "total", Real 42.5 ]

    match projectRowForInsert invoiceStep sourceInvoiceRow emptyIdMappings with
    | Ok _ -> failwith "Expected projection to fail when account ID mapping is missing"
    | Error error -> Assert.Contains("No ID mappings are available yet for referenced table 'account'", error)

[<Fact>]
let ``drain replay groups entries by transaction and ordering`` () =
  let entries =
    [ mkLogEntry
        4L
        2L
        2L
        Insert
        "invoice"
        (mkRow [ "id", Integer 60; "legacy_account_id", Integer 10; "total", Real 42.5 ])
      mkLogEntry 2L 1L 2L Update "invoice" (mkRow [ "id", Integer 50; "total", Real 99.0 ])
      mkLogEntry 3L 2L 1L Insert "account" (mkRow [ "id", Integer 10; "name", String "Alice" ])
      mkLogEntry 1L 1L 1L Insert "account" (mkRow [ "id", Integer 9; "name", String "Bob" ]) ]

  let grouped = groupEntriesByTransaction entries

  Assert.Equal<int64 list>([ 1L; 2L ], grouped |> List.map fst)

  let txn1 =
    grouped |> List.find (fun (txnId, _) -> txnId = 1L) |> snd |> List.map _.id

  let txn2 =
    grouped |> List.find (fun (txnId, _) -> txnId = 2L) |> snd |> List.map _.id

  Assert.Equal<int64 list>([ 1L; 2L ], txn1)
  Assert.Equal<int64 list>([ 3L; 4L ], txn2)

[<Fact>]
let ``drain replay reads migration log entries and existing id mappings`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_drainreplay_read_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore
  let dbPath = Path.Combine(tempDir, "read.db")

  use setupConn = new SqliteConnection($"Data Source={dbPath}")
  setupConn.Open()

  [ "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);"
    "CREATE TABLE _id_mapping(table_name TEXT NOT NULL, old_id INTEGER NOT NULL, new_id INTEGER NOT NULL, PRIMARY KEY(table_name, old_id));"
    "INSERT INTO _migration_log(txn_id, ordering, operation, table_name, row_data) VALUES (7, 1, 'insert', 'legacy_account', '{\"id\":10,\"name\":\"Alice\"}');"
    "INSERT INTO _migration_log(txn_id, ordering, operation, table_name, row_data) VALUES (7, 2, 'insert', 'invoice', '{\"id\":50,\"legacy_account_id\":10,\"total\":42.5}');"
    "INSERT INTO _id_mapping(table_name, old_id, new_id) VALUES ('account', 10, 100);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupConn)
    cmd.ExecuteNonQuery() |> ignore)

  let entriesResult = readMigrationLogEntries setupConn 0L |> fun t -> t.Result

  match entriesResult with
  | Error ex -> failwith $"Expected migration log read to succeed, got {ex.Message}"
  | Ok entries ->
    Assert.Equal(2, entries.Length)
    Assert.Equal(7L, entries.Head.txnId)
    Assert.Equal(Insert, entries.Head.operation)

    match entries.Head.rowData.TryFind "name" with
    | Some(String name) -> Assert.Equal("Alice", name)
    | other -> failwith $"Expected row_data name='Alice', got {other}"

    match entries.Tail.Head.rowData.TryFind "total" with
    | Some(Real total) -> Assert.True(Math.Abs(total - 42.5) < 0.0001)
    | other -> failwith $"Expected row_data total=42.5, got {other}"

  let mappingsResult = loadIdMappings setupConn |> fun t -> t.Result

  match mappingsResult with
  | Error ex -> failwith $"Expected ID mapping load to succeed, got {ex.Message}"
  | Ok mappings ->
    match lookupMappedIdentity "account" [ Integer 10 ] mappings with
    | Ok [ Integer mapped ] -> Assert.Equal(100, mapped)
    | Ok other -> failwith $"Expected mapped identity [Integer 100], got {other}"
    | Error error -> failwith $"Expected account mapping to exist, got error: {error}"

  setupConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``drain replay replays insert update and delete with id translation`` () =
  let sourceSchema =
    { emptyFile with
        tables =
          [ mkTable
              "legacy_account"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ] ]
              []
            mkTable
              "invoice"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "legacy_account_id" SqlInteger [ NotNull; mkForeignKey "legacy_account" [ "id" ] ]
                mkColumn "total" SqlReal [ NotNull ] ]
              [] ] }

  let targetSchema =
    { emptyFile with
        tables =
          [ mkTable
              "account"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ] ]
              []
            mkTable
              "invoice"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "account_id" SqlInteger [ NotNull; mkForeignKey "account" [ "id" ] ]
                mkColumn "total" SqlReal [ NotNull ] ]
              [] ] }

  match buildBulkCopyPlan sourceSchema targetSchema with
  | Error error -> failwith $"bulk copy plan failed: {error}"
  | Ok plan ->
    let tempDir =
      Path.Combine(Path.GetTempPath(), $"mig_drainreplay_apply_{Guid.NewGuid()}")

    Directory.CreateDirectory tempDir |> ignore
    let dbPath = Path.Combine(tempDir, "apply.db")

    use conn = new SqliteConnection($"Data Source={dbPath}")
    conn.Open()

    [ "PRAGMA foreign_keys = ON;"
      "CREATE TABLE account(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);"
      "CREATE TABLE invoice(id INTEGER PRIMARY KEY AUTOINCREMENT, account_id INTEGER NOT NULL, total REAL NOT NULL, FOREIGN KEY(account_id) REFERENCES account(id));"
      "CREATE TABLE _id_mapping(table_name TEXT NOT NULL, old_id INTEGER NOT NULL, new_id INTEGER NOT NULL, PRIMARY KEY(table_name, old_id));" ]
    |> List.iter (fun sql ->
      use cmd = new SqliteCommand(sql, conn)
      cmd.ExecuteNonQuery() |> ignore)

    let insertAndUpdateEntries =
      [ mkLogEntry 1L 11L 1L Insert "legacy_account" (mkRow [ "id", Integer 10; "name", String "Alice" ])
        mkLogEntry
          2L
          11L
          2L
          Insert
          "invoice"
          (mkRow [ "id", Integer 50; "legacy_account_id", Integer 10; "total", Real 42.5 ])
        mkLogEntry
          3L
          12L
          1L
          Update
          "invoice"
          (mkRow [ "id", Integer 50; "legacy_account_id", Integer 10; "total", Real 99.0 ]) ]

    let replayResult =
      replayDrainEntries conn plan insertAndUpdateEntries emptyIdMappings
      |> fun t -> t.Result

    let mappings =
      match replayResult with
      | Error ex -> failwith $"Expected replay to succeed, got {ex.Message}"
      | Ok value -> value

    use accountCmd = new SqliteCommand("SELECT id, name FROM account ORDER BY id", conn)
    use accountReader = accountCmd.ExecuteReader()
    Assert.True(accountReader.Read())
    let accountId = accountReader.GetInt64(0)
    Assert.Equal("Alice", accountReader.GetString(1))
    Assert.False(accountReader.Read())

    use invoiceCmd =
      new SqliteCommand("SELECT id, account_id, total FROM invoice ORDER BY id", conn)

    use invoiceReader = invoiceCmd.ExecuteReader()
    Assert.True(invoiceReader.Read())
    let invoiceId = invoiceReader.GetInt64(0)
    Assert.Equal(accountId, invoiceReader.GetInt64(1))
    Assert.True(Math.Abs(invoiceReader.GetDouble(2) - 99.0) < 0.0001)
    Assert.False(invoiceReader.Read())

    use accountMapCmd =
      new SqliteCommand("SELECT new_id FROM _id_mapping WHERE table_name = 'account' AND old_id = 10", conn)

    let mappedAccountId = accountMapCmd.ExecuteScalar() |> unbox<int64>
    Assert.Equal(accountId, mappedAccountId)

    use invoiceMapCmd =
      new SqliteCommand("SELECT new_id FROM _id_mapping WHERE table_name = 'invoice' AND old_id = 50", conn)

    let mappedInvoiceId = invoiceMapCmd.ExecuteScalar() |> unbox<int64>
    Assert.Equal(invoiceId, mappedInvoiceId)

    match lookupMappedIdentity "account" [ Integer 10 ] mappings with
    | Ok [ Integer mapped ] -> Assert.Equal(accountId, int64 mapped)
    | Ok other -> failwith $"Expected mapped account identity, got {other}"
    | Error error -> failwith $"Expected mapped account identity, got error: {error}"

    let deleteResult =
      replayDrainEntries conn plan [ mkLogEntry 4L 13L 1L Delete "invoice" (mkRow [ "id", Integer 50 ]) ] mappings
      |> fun t -> t.Result

    match deleteResult with
    | Error ex -> failwith $"Expected delete replay to succeed, got {ex.Message}"
    | Ok _ -> ()

    use countInvoiceCmd = new SqliteCommand("SELECT COUNT(*) FROM invoice", conn)
    let invoiceCount = countInvoiceCmd.ExecuteScalar() |> unbox<int64>
    Assert.Equal(0L, invoiceCount)

    conn.Close()
    Directory.Delete(tempDir, true)

[<Fact>]
let ``drain replay rolls back a transaction group when one operation fails`` () =
  let sourceSchema =
    { emptyFile with
        tables =
          [ mkTable
              "legacy_account"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ] ]
              []
            mkTable
              "invoice"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "legacy_account_id" SqlInteger [ NotNull; mkForeignKey "legacy_account" [ "id" ] ]
                mkColumn "total" SqlReal [ NotNull ] ]
              [] ] }

  let targetSchema =
    { emptyFile with
        tables =
          [ mkTable
              "account"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "name" SqlText [ NotNull ] ]
              []
            mkTable
              "invoice"
              [ mkColumn
                  "id"
                  SqlInteger
                  [ PrimaryKey
                      { constraintName = None
                        columns = []
                        isAutoincrement = true } ]
                mkColumn "account_id" SqlInteger [ NotNull; mkForeignKey "account" [ "id" ] ]
                mkColumn "total" SqlReal [ NotNull ] ]
              [] ] }

  match buildBulkCopyPlan sourceSchema targetSchema with
  | Error error -> failwith $"bulk copy plan failed: {error}"
  | Ok plan ->
    let tempDir =
      Path.Combine(Path.GetTempPath(), $"mig_drainreplay_rollback_{Guid.NewGuid()}")

    Directory.CreateDirectory tempDir |> ignore
    let dbPath = Path.Combine(tempDir, "rollback.db")

    use conn = new SqliteConnection($"Data Source={dbPath}")
    conn.Open()

    [ "PRAGMA foreign_keys = ON;"
      "CREATE TABLE account(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);"
      "CREATE TABLE invoice(id INTEGER PRIMARY KEY AUTOINCREMENT, account_id INTEGER NOT NULL, total REAL NOT NULL, FOREIGN KEY(account_id) REFERENCES account(id));"
      "CREATE TABLE _id_mapping(table_name TEXT NOT NULL, old_id INTEGER NOT NULL, new_id INTEGER NOT NULL, PRIMARY KEY(table_name, old_id));" ]
    |> List.iter (fun sql ->
      use cmd = new SqliteCommand(sql, conn)
      cmd.ExecuteNonQuery() |> ignore)

    let entries =
      [ mkLogEntry 1L 21L 1L Insert "legacy_account" (mkRow [ "id", Integer 30; "name", String "Bob" ])
        mkLogEntry
          2L
          21L
          2L
          Insert
          "invoice"
          (mkRow [ "id", Integer 70; "legacy_account_id", Integer 999; "total", Real 10.0 ]) ]

    let replayResult =
      replayDrainEntries conn plan entries emptyIdMappings |> fun t -> t.Result

    match replayResult with
    | Ok _ -> failwith "Expected replay to fail when FK mapping is missing"
    | Error ex ->
      Assert.Contains("Missing ID mapping for FK", ex.Message)
      Assert.Contains("invoice", ex.Message)

    use countAccountCmd = new SqliteCommand("SELECT COUNT(*) FROM account", conn)
    let accountCount = countAccountCmd.ExecuteScalar() |> unbox<int64>
    Assert.Equal(0L, accountCount)

    use countInvoiceCmd = new SqliteCommand("SELECT COUNT(*) FROM invoice", conn)
    let invoiceCount = countInvoiceCmd.ExecuteScalar() |> unbox<int64>
    Assert.Equal(0L, invoiceCount)

    use countMappingCmd = new SqliteCommand("SELECT COUNT(*) FROM _id_mapping", conn)
    let mappingCount = countMappingCmd.ExecuteScalar() |> unbox<int64>
    Assert.Equal(0L, mappingCount)

    conn.Close()
    Directory.Delete(tempDir, true)

[<Fact>]
let ``migration status reports old and new database markers and counts`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_status_report_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath = Path.Combine(tempDir, "old.db")
  let newDbPath = Path.Combine(tempDir, "new.db")

  use oldConn = new SqliteConnection($"Data Source={oldDbPath}")
  oldConn.Open()

  [ "CREATE TABLE _migration_marker(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_marker(id, status) VALUES (0, 'recording');"
    "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);"
    "INSERT INTO _migration_log(txn_id, ordering, operation, table_name, row_data) VALUES (1, 1, 'insert', 'student', '{\"id\":1,\"name\":\"A\"}');"
    "INSERT INTO _migration_log(txn_id, ordering, operation, table_name, row_data) VALUES (1, 2, 'update', 'student', '{\"id\":1,\"name\":\"B\"}');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, oldConn)
    cmd.ExecuteNonQuery() |> ignore)

  use newConn = new SqliteConnection($"Data Source={newDbPath}")
  newConn.Open()

  [ "CREATE TABLE _migration_status(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_status(id, status) VALUES (0, 'migrating');"
    "CREATE TABLE _schema_identity(id INTEGER PRIMARY KEY CHECK (id = 0), schema_hash TEXT NOT NULL, schema_commit TEXT, created_utc TEXT NOT NULL);"
    "INSERT INTO _schema_identity(id, schema_hash, schema_commit, created_utc) VALUES (0, '1111222233334444', 'abc1234', '2026-02-18T00:00:00.0000000Z');"
    "CREATE TABLE _migration_progress(id INTEGER PRIMARY KEY CHECK (id = 0), last_replayed_log_id INTEGER NOT NULL, drain_completed INTEGER NOT NULL);"
    "INSERT INTO _migration_progress(id, last_replayed_log_id, drain_completed) VALUES (0, 1, 0);"
    "CREATE TABLE _id_mapping(table_name TEXT NOT NULL, old_id INTEGER NOT NULL, new_id INTEGER NOT NULL, PRIMARY KEY(table_name, old_id));"
    "INSERT INTO _id_mapping(table_name, old_id, new_id) VALUES ('student', 1, 101);"
    "INSERT INTO _id_mapping(table_name, old_id, new_id) VALUES ('student', 2, 102);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, newConn)
    cmd.ExecuteNonQuery() |> ignore)

  let statusResult = getStatus oldDbPath (Some newDbPath) |> fun t -> t.Result

  match statusResult with
  | Error ex -> failwith $"Expected status read to succeed, got {ex.Message}"
  | Ok report ->
    Assert.Equal(Some "recording", report.oldMarkerStatus)
    Assert.Equal(2L, report.migrationLogEntries)
    Assert.Equal(Some 1L, report.pendingReplayEntries)
    Assert.Equal(Some 2L, report.idMappingEntries)
    Assert.Equal(Some "migrating", report.newMigrationStatus)
    Assert.Equal(Some true, report.idMappingTablePresent)
    Assert.Equal(Some true, report.migrationProgressTablePresent)
    Assert.Equal(Some "1111222233334444", report.schemaIdentityHash)
    Assert.Equal(Some "abc1234", report.schemaIdentityCommit)

  oldConn.Close()
  newConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``migration status handles databases without migration tables`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_status_nomarker_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath = Path.Combine(tempDir, "old.db")

  use oldConn = new SqliteConnection($"Data Source={oldDbPath}")
  oldConn.Open()

  use initCmd =
    new SqliteCommand("CREATE TABLE student(id INTEGER PRIMARY KEY, name TEXT NOT NULL);", oldConn)

  initCmd.ExecuteNonQuery() |> ignore

  let statusResult = getStatus oldDbPath None |> fun t -> t.Result

  match statusResult with
  | Error ex -> failwith $"Expected status read without migration tables to succeed, got {ex.Message}"
  | Ok report ->
    Assert.Equal(None, report.oldMarkerStatus)
    Assert.Equal(0L, report.migrationLogEntries)
    Assert.Equal(None, report.pendingReplayEntries)
    Assert.Equal(None, report.idMappingEntries)
    Assert.Equal(None, report.newMigrationStatus)
    Assert.Equal(None, report.idMappingTablePresent)
    Assert.Equal(None, report.migrationProgressTablePresent)
    Assert.Equal(None, report.schemaIdentityHash)
    Assert.Equal(None, report.schemaIdentityCommit)

  oldConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``migration status reports cleanup state after cutover`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_status_cutover_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath = Path.Combine(tempDir, "old.db")
  let newDbPath = Path.Combine(tempDir, "new.db")

  use oldConn = new SqliteConnection($"Data Source={oldDbPath}")
  oldConn.Open()

  [ "CREATE TABLE _migration_marker(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_marker(id, status) VALUES (0, 'draining');"
    "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);"
    "INSERT INTO _migration_log(txn_id, ordering, operation, table_name, row_data) VALUES (1, 1, 'insert', 'student', '{\"id\":1,\"name\":\"A\"}');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, oldConn)
    cmd.ExecuteNonQuery() |> ignore)

  use newConn = new SqliteConnection($"Data Source={newDbPath}")
  newConn.Open()

  [ "CREATE TABLE _migration_status(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_status(id, status) VALUES (0, 'ready');"
    "CREATE TABLE _schema_identity(id INTEGER PRIMARY KEY CHECK (id = 0), schema_hash TEXT NOT NULL, schema_commit TEXT, created_utc TEXT NOT NULL);"
    "INSERT INTO _schema_identity(id, schema_hash, schema_commit, created_utc) VALUES (0, '9999aaaabbbbcccc', 'deadbeef', '2026-02-18T00:00:00.0000000Z');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, newConn)
    cmd.ExecuteNonQuery() |> ignore)

  let statusResult = getStatus oldDbPath (Some newDbPath) |> fun t -> t.Result

  match statusResult with
  | Error ex -> failwith $"Expected status read after cutover cleanup to succeed, got {ex.Message}"
  | Ok report ->
    Assert.Equal(Some "draining", report.oldMarkerStatus)
    Assert.Equal(1L, report.migrationLogEntries)
    Assert.Equal(Some "ready", report.newMigrationStatus)
    Assert.Equal(Some 0L, report.pendingReplayEntries)
    Assert.Equal(Some 0L, report.idMappingEntries)
    Assert.Equal(Some false, report.idMappingTablePresent)
    Assert.Equal(Some false, report.migrationProgressTablePresent)
    Assert.Equal(Some "9999aaaabbbbcccc", report.schemaIdentityHash)
    Assert.Equal(Some "deadbeef", report.schemaIdentityCommit)

  oldConn.Close()
  newConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cutover sets ready status and drops id mapping and progress tables`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cutover_success_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let newDbPath = Path.Combine(tempDir, "new.db")

  use newConn = new SqliteConnection($"Data Source={newDbPath}")
  newConn.Open()

  [ "CREATE TABLE _migration_status(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_status(id, status) VALUES (0, 'migrating');"
    "CREATE TABLE _migration_progress(id INTEGER PRIMARY KEY CHECK (id = 0), last_replayed_log_id INTEGER NOT NULL, drain_completed INTEGER NOT NULL);"
    "INSERT INTO _migration_progress(id, last_replayed_log_id, drain_completed) VALUES (0, 10, 1);"
    "CREATE TABLE _id_mapping(table_name TEXT NOT NULL, old_id INTEGER NOT NULL, new_id INTEGER NOT NULL, PRIMARY KEY(table_name, old_id));"
    "INSERT INTO _id_mapping(table_name, old_id, new_id) VALUES ('student', 1, 101);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, newConn)
    cmd.ExecuteNonQuery() |> ignore)

  let cutoverResult = runCutover newDbPath |> fun t -> t.Result

  match cutoverResult with
  | Error ex -> failwith $"Expected cutover to succeed, got {ex.Message}"
  | Ok result ->
    Assert.Equal("migrating", result.previousStatus)
    Assert.True(result.idMappingDropped)
    Assert.True(result.migrationProgressDropped)

  use verifyStatusCmd =
    new SqliteCommand("SELECT status FROM _migration_status WHERE id = 0", newConn)

  let statusValue = verifyStatusCmd.ExecuteScalar() |> string
  Assert.Equal("ready", statusValue)

  use existsCmd =
    new SqliteCommand("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_id_mapping' LIMIT 1", newConn)

  let idMappingExists = existsCmd.ExecuteScalar()
  Assert.True(isNull idMappingExists)

  use progressExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_progress' LIMIT 1",
      newConn
    )

  let progressExists = progressExistsCmd.ExecuteScalar()
  Assert.True(isNull progressExists)

  newConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cutover is idempotent when migration status is already ready`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cutover_ready_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let newDbPath = Path.Combine(tempDir, "new.db")

  use newConn = new SqliteConnection($"Data Source={newDbPath}")
  newConn.Open()

  [ "CREATE TABLE _migration_status(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_status(id, status) VALUES (0, 'ready');"
    "CREATE TABLE _schema_identity(id INTEGER PRIMARY KEY CHECK (id = 0), schema_hash TEXT NOT NULL, schema_commit TEXT, created_utc TEXT NOT NULL);"
    "INSERT INTO _schema_identity(id, schema_hash, schema_commit, created_utc) VALUES (0, '9999aaaabbbbcccc', 'deadbeef', '2026-02-18T00:00:00.0000000Z');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, newConn)
    cmd.ExecuteNonQuery() |> ignore)

  let cutoverResult = runCutover newDbPath |> fun t -> t.Result

  match cutoverResult with
  | Error ex -> failwith $"Expected idempotent cutover to succeed, got {ex.Message}"
  | Ok result ->
    Assert.Equal("ready", result.previousStatus)
    Assert.False(result.idMappingDropped)
    Assert.False(result.migrationProgressDropped)

  newConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cutover fails when migration status table is missing`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cutover_missing_status_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let newDbPath = Path.Combine(tempDir, "new.db")

  use newConn = new SqliteConnection($"Data Source={newDbPath}")
  newConn.Open()

  use initCmd =
    new SqliteCommand(
      "CREATE TABLE _id_mapping(table_name TEXT NOT NULL, old_id INTEGER NOT NULL, new_id INTEGER NOT NULL, PRIMARY KEY(table_name, old_id));",
      newConn
    )

  initCmd.ExecuteNonQuery() |> ignore

  let cutoverResult = runCutover newDbPath |> fun t -> t.Result

  match cutoverResult with
  | Ok _ -> failwith "Expected cutover to fail when _migration_status table is missing"
  | Error ex -> Assert.Contains("_migration_status table is missing", ex.Message)

  newConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cutover fails when drain has not completed`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cutover_not_drained_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let newDbPath = Path.Combine(tempDir, "new.db")

  use newConn = new SqliteConnection($"Data Source={newDbPath}")
  newConn.Open()

  [ "CREATE TABLE _migration_status(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_status(id, status) VALUES (0, 'migrating');"
    "CREATE TABLE _migration_progress(id INTEGER PRIMARY KEY CHECK (id = 0), last_replayed_log_id INTEGER NOT NULL, drain_completed INTEGER NOT NULL);"
    "INSERT INTO _migration_progress(id, last_replayed_log_id, drain_completed) VALUES (0, 100, 0);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, newConn)
    cmd.ExecuteNonQuery() |> ignore)

  let cutoverResult = runCutover newDbPath |> fun t -> t.Result

  match cutoverResult with
  | Ok _ -> failwith "Expected cutover to fail when drain is not complete"
  | Error ex -> Assert.Contains("Drain is not complete", ex.Message)

  newConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cleanup old drops migration marker and log tables`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cleanup_old_success_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath = Path.Combine(tempDir, "old.db")

  use oldConn = new SqliteConnection($"Data Source={oldDbPath}")
  oldConn.Open()

  [ "CREATE TABLE _migration_marker(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_marker(id, status) VALUES (0, 'draining');"
    "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);"
    "INSERT INTO _migration_log(txn_id, ordering, operation, table_name, row_data) VALUES (1, 1, 'insert', 'student', '{\"id\":1,\"name\":\"A\"}');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, oldConn)
    cmd.ExecuteNonQuery() |> ignore)

  let cleanupResult = runCleanupOld oldDbPath |> fun t -> t.Result

  match cleanupResult with
  | Error ex -> failwith $"Expected cleanup old to succeed, got {ex.Message}"
  | Ok result ->
    Assert.Equal(Some "draining", result.previousMarkerStatus)
    Assert.True(result.markerDropped)
    Assert.True(result.logDropped)

  use markerExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_marker' LIMIT 1",
      oldConn
    )

  let markerExists = markerExistsCmd.ExecuteScalar()
  Assert.True(isNull markerExists)

  use logExistsCmd =
    new SqliteCommand("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_log' LIMIT 1", oldConn)

  let logExists = logExistsCmd.ExecuteScalar()
  Assert.True(isNull logExists)

  oldConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cleanup old is idempotent when migration tables are missing`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cleanup_old_missing_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath = Path.Combine(tempDir, "old.db")

  use oldConn = new SqliteConnection($"Data Source={oldDbPath}")
  oldConn.Open()

  use initCmd =
    new SqliteCommand("CREATE TABLE student(id INTEGER PRIMARY KEY, name TEXT NOT NULL);", oldConn)

  initCmd.ExecuteNonQuery() |> ignore

  let cleanupResult = runCleanupOld oldDbPath |> fun t -> t.Result

  match cleanupResult with
  | Error ex -> failwith $"Expected idempotent cleanup old to succeed, got {ex.Message}"
  | Ok result ->
    Assert.Equal(None, result.previousMarkerStatus)
    Assert.False(result.markerDropped)
    Assert.False(result.logDropped)

  oldConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cleanup old fails while marker status is recording`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cleanup_old_recording_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath = Path.Combine(tempDir, "old.db")

  use oldConn = new SqliteConnection($"Data Source={oldDbPath}")
  oldConn.Open()

  [ "CREATE TABLE _migration_marker(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_marker(id, status) VALUES (0, 'recording');"
    "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, oldConn)
    cmd.ExecuteNonQuery() |> ignore)

  let cleanupResult = runCleanupOld oldDbPath |> fun t -> t.Result

  match cleanupResult with
  | Ok _ -> failwith "Expected cleanup old to fail while marker status is recording"
  | Error ex -> Assert.Contains("recording mode", ex.Message)

  oldConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli status prints cutover-complete cleanup state`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_status_cutover_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dirName = DirectoryInfo(tempDir).Name
  let oldDbPath = Path.Combine(tempDir, $"{dirName}-1111222233334444.sqlite")
  let schemaPath = Path.Combine(tempDir, "schema.fsx")
  let migLibPath = typeof<AutoIncPKAttribute>.Assembly.Location.Replace("\\", "\\\\")

  let script =
    $"""
#r @"{migLibPath}"

open MigLib.Db

[<AutoIncPK "id">]
type Student = {{ id: int64; name: string }}
"""

  File.WriteAllText(schemaPath, script.Trim())
  let newDbPath = deriveDeterministicNewDbPathFromSchema tempDir schemaPath

  use oldConn = new SqliteConnection($"Data Source={oldDbPath}")
  oldConn.Open()

  [ "CREATE TABLE _migration_marker(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_marker(id, status) VALUES (0, 'draining');"
    "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);"
    "INSERT INTO _migration_log(txn_id, ordering, operation, table_name, row_data) VALUES (1, 1, 'insert', 'student', '{\"id\":1,\"name\":\"A\"}');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, oldConn)
    cmd.ExecuteNonQuery() |> ignore)

  use newConn = new SqliteConnection($"Data Source={newDbPath}")
  newConn.Open()

  [ "CREATE TABLE _migration_status(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_status(id, status) VALUES (0, 'ready');"
    "CREATE TABLE _schema_identity(id INTEGER PRIMARY KEY CHECK (id = 0), schema_hash TEXT NOT NULL, schema_commit TEXT, created_utc TEXT NOT NULL);"
    "INSERT INTO _schema_identity(id, schema_hash, schema_commit, created_utc) VALUES (0, '9999aaaabbbbcccc', 'deadbeef', '2026-02-18T00:00:00.0000000Z');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, newConn)
    cmd.ExecuteNonQuery() |> ignore)

  let exitCode, stdOut, stdErr = runMigCliInDirectory (Some tempDir) [ "status" ]

  Assert.Equal(0, exitCode)
  Assert.Contains($"Old database: {oldDbPath}", stdOut)
  Assert.Contains("Marker status: draining", stdOut)
  Assert.Contains("Migration status: ready", stdOut)
  Assert.Contains("Schema hash: 9999aaaabbbbcccc", stdOut)
  Assert.Contains("Schema commit: deadbeef", stdOut)
  Assert.Contains("Pending replay entries: 0 (cutover complete)", stdOut)
  Assert.Contains("_id_mapping: removed", stdOut)
  Assert.Contains("_migration_progress: removed", stdOut)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")

  oldConn.Close()
  newConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli status supports inferred new-only inspection`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_status_new_only_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let schemaPath = Path.Combine(tempDir, "schema.fsx")
  let migLibPath = typeof<AutoIncPKAttribute>.Assembly.Location.Replace("\\", "\\\\")

  let script =
    $"""
#r @"{migLibPath}"

open MigLib.Db

[<AutoIncPK "id">]
type Student = {{ id: int64; name: string }}
"""

  File.WriteAllText(schemaPath, script.Trim())
  let newDbPath = deriveDeterministicNewDbPathFromSchema tempDir schemaPath

  use newConn = new SqliteConnection($"Data Source={newDbPath}")
  newConn.Open()

  [ "CREATE TABLE _migration_status(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_status(id, status) VALUES (0, 'ready');"
    "CREATE TABLE _schema_identity(id INTEGER PRIMARY KEY CHECK (id = 0), schema_hash TEXT NOT NULL, schema_commit TEXT, created_utc TEXT NOT NULL);"
    "INSERT INTO _schema_identity(id, schema_hash, schema_commit, created_utc) VALUES (0, 'abcddcba12344321', 'cafebabe', '2026-02-18T00:00:00.0000000Z');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, newConn)
    cmd.ExecuteNonQuery() |> ignore)

  let exitCode, stdOut, stdErr = runMigCliInDirectory (Some tempDir) [ "status" ]

  Assert.Equal(0, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
  Assert.Contains("Old database: n/a (not inferred)", stdOut)
  Assert.Contains("Marker status: n/a", stdOut)
  Assert.Contains("Migration log entries: n/a", stdOut)
  Assert.Contains($"New database: {newDbPath}", stdOut)
  Assert.Contains("Migration status: ready", stdOut)
  Assert.Contains("Schema hash: abcddcba12344321", stdOut)
  Assert.Contains("Schema commit: cafebabe", stdOut)
  Assert.Contains("Pending replay entries: n/a (old database unavailable)", stdOut)
  Assert.Contains("_id_mapping: removed", stdOut)
  Assert.Contains("_migration_progress: removed", stdOut)

  newConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli root help shows current command surface`` () =
  assertCliHelpOutput
    [ "--help" ]
    "USAGE: mig [--help] [<subcommand> [<options>]]"
    [ "migrate <options>"
      "drain <options>"
      "cutover <options>"
      "cleanup-old <options>"
      "status <options>" ]

[<Fact>]
let ``cli subcommand help shows usage and options`` () =
  let cases: (string list * string * string list) list =
    [ ([ "migrate"; "--help" ], "USAGE: mig migrate [--help] [--dir <path>]", [ "--dir, -d <path>" ])
      ([ "drain"; "--help" ], "USAGE: mig drain [--help] [--dir <path>]", [ "--dir, -d <path>" ])
      ([ "cutover"; "--help" ], "USAGE: mig cutover [--help] [--dir <path>]", [ "--dir, -d <path>" ])
      ([ "cleanup-old"; "--help" ], "USAGE: mig cleanup-old [--help] [--dir <path>]", [ "--dir, -d <path>" ])
      ([ "status"; "--help" ], "USAGE: mig status [--help] [--dir <path>]", [ "--dir, -d <path>" ]) ]

  for args, expectedUsage, expectedFragments in cases do
    assertCliHelpOutput args expectedUsage expectedFragments

[<Fact>]
let ``cli cutover returns error when drain not complete`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_cutover_not_drained_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let schemaPath = Path.Combine(tempDir, "schema.fsx")
  let migLibPath = typeof<AutoIncPKAttribute>.Assembly.Location.Replace("\\", "\\\\")

  let script =
    $"""
#r @"{migLibPath}"

open MigLib.Db

[<AutoIncPK "id">]
type Student = {{ id: int64; name: string }}
"""

  File.WriteAllText(schemaPath, script.Trim())
  let newDbPath = deriveDeterministicNewDbPathFromSchema tempDir schemaPath

  use newConn = new SqliteConnection($"Data Source={newDbPath}")
  newConn.Open()

  [ "CREATE TABLE _migration_status(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_status(id, status) VALUES (0, 'migrating');"
    "CREATE TABLE _migration_progress(id INTEGER PRIMARY KEY CHECK (id = 0), last_replayed_log_id INTEGER NOT NULL, drain_completed INTEGER NOT NULL);"
    "INSERT INTO _migration_progress(id, last_replayed_log_id, drain_completed) VALUES (0, 12, 0);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, newConn)
    cmd.ExecuteNonQuery() |> ignore)

  let exitCode, stdOut, stdErr = runMigCli [ "cutover"; "-d"; tempDir ]

  Assert.Equal(1, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdOut, $"Expected no stdout output, got: {stdOut}")
  Assert.Contains("cutover failed: Drain is not complete", stdErr)

  newConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli cleanup-old prints dropped table summary`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_cleanup_old_success_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dirName = DirectoryInfo(tempDir).Name
  let oldDbPath = Path.Combine(tempDir, $"{dirName}-99990000aaaabbbb.sqlite")

  use oldConn = new SqliteConnection($"Data Source={oldDbPath}")
  oldConn.Open()

  [ "CREATE TABLE _migration_marker(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_marker(id, status) VALUES (0, 'draining');"
    "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, oldConn)
    cmd.ExecuteNonQuery() |> ignore)

  oldConn.Close()

  let exitCode, stdOut, stdErr = runMigCli [ "cleanup-old"; "-d"; tempDir ]

  Assert.Equal(0, exitCode)
  Assert.Contains("Old database cleanup complete.", stdOut)
  Assert.Contains($"Old database: {oldDbPath}", stdOut)
  Assert.Contains("Previous marker status: draining", stdOut)
  Assert.Contains("Dropped _migration_marker: yes", stdOut)
  Assert.Contains("Dropped _migration_log: yes", stdOut)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")

  use verifyConn = new SqliteConnection($"Data Source={oldDbPath}")
  verifyConn.Open()

  use markerExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_marker' LIMIT 1",
      verifyConn
    )

  let markerExists = markerExistsCmd.ExecuteScalar()
  Assert.True(isNull markerExists)

  use logExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_log' LIMIT 1",
      verifyConn
    )

  let logExists = logExistsCmd.ExecuteScalar()
  Assert.True(isNull logExists)

  verifyConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli cleanup-old returns error while recording`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_cleanup_old_recording_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dirName = DirectoryInfo(tempDir).Name
  let oldDbPath = Path.Combine(tempDir, $"{dirName}-ccccddddeeeeffff.sqlite")

  use oldConn = new SqliteConnection($"Data Source={oldDbPath}")
  oldConn.Open()

  [ "CREATE TABLE _migration_marker(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"
    "INSERT INTO _migration_marker(id, status) VALUES (0, 'recording');"
    "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, oldConn)
    cmd.ExecuteNonQuery() |> ignore)

  oldConn.Close()

  let exitCode, stdOut, stdErr = runMigCli [ "cleanup-old"; "-d"; tempDir ]

  Assert.Equal(1, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdOut, $"Expected no stdout output, got: {stdOut}")
  Assert.Contains("cleanup-old failed: Old database is still in recording mode.", stdErr)

  use verifyConn = new SqliteConnection($"Data Source={oldDbPath}")
  verifyConn.Open()

  use markerExistsCmd =
    new SqliteCommand(
      "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '_migration_marker' LIMIT 1",
      verifyConn
    )

  let markerExists = markerExistsCmd.ExecuteScalar()
  Assert.False(isNull markerExists)

  verifyConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli migrate derives deterministic new path from current directory schema hash`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_migrate_deterministic_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dirName = DirectoryInfo(tempDir).Name
  let oldDbPath = Path.Combine(tempDir, $"{dirName}-0123456789abcdef.sqlite")
  let schemaPath = Path.Combine(tempDir, "schema.fsx")

  use setupOldConn = new SqliteConnection($"Data Source={oldDbPath}")
  setupOldConn.Open()

  [ "PRAGMA foreign_keys = ON;"
    "CREATE TABLE student(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);"
    "INSERT INTO student(id, name) VALUES (1, 'Alice');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupOldConn)
    cmd.ExecuteNonQuery() |> ignore)

  let migLibPath = typeof<AutoIncPKAttribute>.Assembly.Location.Replace("\\", "\\\\")

  let script =
    $"""
#r @"{migLibPath}"

open MigLib.Db

[<AutoIncPK "id">]
type Student = {{ id: int64; name: string }}
"""

  File.WriteAllText(schemaPath, script.Trim())

  let expectedNewDbPath = deriveDeterministicNewDbPathFromSchema tempDir schemaPath

  setupOldConn.Close()

  let exitCode, stdOut, stdErr = runMigCli [ "migrate"; "-d"; tempDir ]

  Assert.Equal(0, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
  Assert.Contains($"New database: {expectedNewDbPath}", stdOut)
  Assert.True(File.Exists expectedNewDbPath)

  use verifyConn = new SqliteConnection($"Data Source={expectedNewDbPath}")
  verifyConn.Open()
  use studentCountCmd = new SqliteCommand("SELECT COUNT(*) FROM student", verifyConn)
  let studentCount = studentCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(1L, studentCount)

  verifyConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli migrate stores schema commit metadata automatically`` () =
  let gitRoot = findGitRootOrFail (Directory.GetCurrentDirectory())
  let repoHeadCommit = gitHeadCommitOrFail gitRoot

  let tempRoot = Path.Combine(gitRoot, ".tmp_mig_tests")
  Directory.CreateDirectory tempRoot |> ignore

  let tempDir =
    Path.Combine(tempRoot, $"mig_cli_migrate_schema_commit_auto_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dirName = DirectoryInfo(tempDir).Name
  let oldDbPath = Path.Combine(tempDir, $"{dirName}-1111222233334444.sqlite")
  let schemaPath = Path.Combine(tempDir, "schema.fsx")

  use setupOldConn = new SqliteConnection($"Data Source={oldDbPath}")
  setupOldConn.Open()

  [ "PRAGMA foreign_keys = ON;"
    "CREATE TABLE student(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);"
    "INSERT INTO student(id, name) VALUES (1, 'Alice');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupOldConn)
    cmd.ExecuteNonQuery() |> ignore)

  let migLibPath = typeof<AutoIncPKAttribute>.Assembly.Location.Replace("\\", "\\\\")

  let script =
    $"""
#r @"{migLibPath}"

open MigLib.Db

[<AutoIncPK "id">]
type Student = {{ id: int64; name: string }}
"""

  File.WriteAllText(schemaPath, script.Trim())
  let expectedNewDbPath = deriveDeterministicNewDbPathFromSchema tempDir schemaPath
  setupOldConn.Close()

  let exitCode, stdOut, stdErr = runMigCli [ "migrate"; "-d"; tempDir ]

  Assert.Equal(0, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
  Assert.Contains($"New database: {expectedNewDbPath}", stdOut)

  use verifyConn = new SqliteConnection($"Data Source={expectedNewDbPath}")
  verifyConn.Open()

  use schemaIdentityCmd =
    new SqliteCommand("SELECT schema_commit FROM _schema_identity WHERE id = 0", verifyConn)

  let storedSchemaCommit = schemaIdentityCmd.ExecuteScalar() |> string
  Assert.Equal(repoHeadCommit, storedSchemaCommit)

  verifyConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli migrate auto-discovers schema and old db from current directory`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_migrate_auto_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dirName = DirectoryInfo(tempDir).Name
  let oldDbPath = Path.Combine(tempDir, $"{dirName}-fedcba9876543210.sqlite")
  let schemaPath = Path.Combine(tempDir, "schema.fsx")

  use setupOldConn = new SqliteConnection($"Data Source={oldDbPath}")
  setupOldConn.Open()

  [ "PRAGMA foreign_keys = ON;"
    "CREATE TABLE student(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);"
    "INSERT INTO student(id, name) VALUES (1, 'Alice');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupOldConn)
    cmd.ExecuteNonQuery() |> ignore)

  let migLibPath = typeof<AutoIncPKAttribute>.Assembly.Location.Replace("\\", "\\\\")

  let script =
    $"""
#r @"{migLibPath}"

open MigLib.Db

[<AutoIncPK "id">]
type Student = {{ id: int64; name: string }}
"""

  File.WriteAllText(schemaPath, script.Trim())
  let expectedNewDbPath = deriveDeterministicNewDbPathFromSchema tempDir schemaPath
  setupOldConn.Close()

  let exitCode, stdOut, stdErr = runMigCliInDirectory (Some tempDir) [ "migrate" ]

  Assert.Equal(0, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
  Assert.Contains($"Old database: {oldDbPath}", stdOut)
  Assert.Contains($"Schema script: {schemaPath}", stdOut)
  Assert.Contains($"New database: {expectedNewDbPath}", stdOut)
  Assert.True(File.Exists expectedNewDbPath)

  use verifyConn = new SqliteConnection($"Data Source={expectedNewDbPath}")
  verifyConn.Open()
  use studentCountCmd = new SqliteCommand("SELECT COUNT(*) FROM student", verifyConn)
  let studentCount = studentCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(1L, studentCount)

  verifyConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli drain cutover status and cleanup-old auto-discover deterministic paths from current directory`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_operational_auto_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let dirName = DirectoryInfo(tempDir).Name
  let oldDbPath = Path.Combine(tempDir, $"{dirName}-a1b2c3d4e5f60718.sqlite")
  let schemaPath = Path.Combine(tempDir, "schema.fsx")

  use setupOldConn = new SqliteConnection($"Data Source={oldDbPath}")
  setupOldConn.Open()

  [ "PRAGMA foreign_keys = ON;"
    "CREATE TABLE student(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);"
    "INSERT INTO student(id, name) VALUES (1, 'Alice');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupOldConn)
    cmd.ExecuteNonQuery() |> ignore)

  let migLibPath = typeof<AutoIncPKAttribute>.Assembly.Location.Replace("\\", "\\\\")

  let script =
    $"""
#r @"{migLibPath}"

open MigLib.Db

[<AutoIncPK "id">]
type Student = {{ id: int64; name: string }}
"""

  File.WriteAllText(schemaPath, script.Trim())
  let expectedNewDbPath = deriveDeterministicNewDbPathFromSchema tempDir schemaPath
  setupOldConn.Close()

  let migrateExitCode, migrateStdOut, migrateStdErr =
    runMigCliInDirectory (Some tempDir) [ "migrate" ]

  Assert.Equal(0, migrateExitCode)
  Assert.True(String.IsNullOrWhiteSpace migrateStdErr, $"Expected no stderr output, got: {migrateStdErr}")
  Assert.Contains($"Old database: {oldDbPath}", migrateStdOut)
  Assert.Contains($"New database: {expectedNewDbPath}", migrateStdOut)

  let drainExitCode, drainStdOut, drainStdErr =
    runMigCliInDirectory (Some tempDir) [ "drain" ]

  Assert.Equal(0, drainExitCode)
  Assert.True(String.IsNullOrWhiteSpace drainStdErr, $"Expected no stderr output, got: {drainStdErr}")
  Assert.Contains($"Old database: {oldDbPath}", drainStdOut)
  Assert.Contains($"New database: {expectedNewDbPath}", drainStdOut)

  let cutoverExitCode, cutoverStdOut, cutoverStdErr =
    runMigCliInDirectory (Some tempDir) [ "cutover" ]

  Assert.Equal(0, cutoverExitCode)
  Assert.True(String.IsNullOrWhiteSpace cutoverStdErr, $"Expected no stderr output, got: {cutoverStdErr}")
  Assert.Contains($"New database: {expectedNewDbPath}", cutoverStdOut)
  Assert.Contains("Current migration status: ready", cutoverStdOut)

  let statusExitCode, statusStdOut, statusStdErr =
    runMigCliInDirectory (Some tempDir) [ "status" ]

  Assert.Equal(0, statusExitCode)
  Assert.True(String.IsNullOrWhiteSpace statusStdErr, $"Expected no stderr output, got: {statusStdErr}")
  Assert.Contains($"Old database: {oldDbPath}", statusStdOut)
  Assert.Contains($"New database: {expectedNewDbPath}", statusStdOut)
  Assert.Contains("Migration status: ready", statusStdOut)

  let cleanupExitCode, cleanupStdOut, cleanupStdErr =
    runMigCliInDirectory (Some tempDir) [ "cleanup-old" ]

  Assert.Equal(0, cleanupExitCode)
  Assert.True(String.IsNullOrWhiteSpace cleanupStdErr, $"Expected no stderr output, got: {cleanupStdErr}")
  Assert.Contains($"Old database: {oldDbPath}", cleanupStdOut)
  Assert.Contains("Dropped _migration_marker: yes", cleanupStdOut)
  Assert.Contains("Dropped _migration_log: yes", cleanupStdOut)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``cli migrate skips when current-directory schema-matched database already exists`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_cli_migrate_skip_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let schemaPath = Path.Combine(tempDir, "schema.fsx")
  let migLibPath = typeof<AutoIncPKAttribute>.Assembly.Location.Replace("\\", "\\\\")

  let script =
    $"""
#r @"{migLibPath}"

open MigLib.Db

[<AutoIncPK "id">]
type Student = {{ id: int64; name: string }}
"""

  File.WriteAllText(schemaPath, script.Trim())

  let expectedDbPath = deriveDeterministicNewDbPathFromSchema tempDir schemaPath

  use existingConn = new SqliteConnection($"Data Source={expectedDbPath}")
  existingConn.Open()

  use initCmd =
    new SqliteCommand("CREATE TABLE sentinel(id INTEGER PRIMARY KEY, value TEXT NOT NULL);", existingConn)

  initCmd.ExecuteNonQuery() |> ignore
  existingConn.Close()

  let exitCode, stdOut, stdErr = runMigCliInDirectory (Some tempDir) [ "migrate" ]

  Assert.Equal(0, exitCode)
  Assert.True(String.IsNullOrWhiteSpace stdErr, $"Expected no stderr output, got: {stdErr}")
  Assert.Contains("Migrate skipped.", stdOut)
  Assert.Contains($"Database already present for current schema: {expectedDbPath}", stdOut)

  use verifyConn = new SqliteConnection($"Data Source={expectedDbPath}")
  verifyConn.Open()

  use sentinelCountCmd =
    new SqliteCommand("SELECT COUNT(*) FROM sentinel", verifyConn)

  let sentinelCount = sentinelCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(0L, sentinelCount)

  verifyConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``migrate creates new database, copies rows, and sets recording markers`` () =
  let tempDir = Path.Combine(Path.GetTempPath(), $"mig_migrate_flow_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath = Path.Combine(tempDir, "old.db")
  let newDbPath = Path.Combine(tempDir, "new.db")
  let schemaPath = Path.Combine(tempDir, "schema.fsx")

  use setupOldConn = new SqliteConnection($"Data Source={oldDbPath}")
  setupOldConn.Open()

  [ "PRAGMA foreign_keys = ON;"
    "CREATE TABLE account(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);"
    "CREATE TABLE invoice(id INTEGER PRIMARY KEY AUTOINCREMENT, account_id INTEGER NOT NULL, total REAL NOT NULL, FOREIGN KEY(account_id) REFERENCES account(id));"
    "INSERT INTO account(id, name) VALUES (10, 'Alice');"
    "INSERT INTO invoice(id, account_id, total) VALUES (100, 10, 42.5);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupOldConn)
    cmd.ExecuteNonQuery() |> ignore)

  let migLibPath = typeof<AutoIncPKAttribute>.Assembly.Location.Replace("\\", "\\\\")

  let script =
    $"""
#r @"{migLibPath}"

open MigLib.Db

[<AutoIncPK "id">]
type Account = {{ id: int64; name: string }}

[<AutoIncPK "id">]
type Invoice = {{ id: int64; account: Account; total: float }}
"""

  File.WriteAllText(schemaPath, script.Trim())
  let expectedSchemaHash = deriveShortSchemaHashFromScript schemaPath

  let migrateResult = runMigrate oldDbPath schemaPath newDbPath |> fun t -> t.Result

  match migrateResult with
  | Error ex -> failwith $"Expected migrate to succeed, got {ex.Message}"
  | Ok result ->
    Assert.Equal(newDbPath, result.newDbPath)
    Assert.Equal(2, result.copiedTables)
    Assert.Equal(2L, result.copiedRows)

  use verifyOldConn = new SqliteConnection($"Data Source={oldDbPath}")
  verifyOldConn.Open()

  use markerCmd =
    new SqliteCommand("SELECT status FROM _migration_marker WHERE id = 0", verifyOldConn)

  let markerStatus = markerCmd.ExecuteScalar() |> string
  Assert.Equal("recording", markerStatus)

  use oldLogCountCmd =
    new SqliteCommand("SELECT COUNT(*) FROM _migration_log", verifyOldConn)

  let oldLogCount = oldLogCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(0L, oldLogCount)

  use verifyNewConn = new SqliteConnection($"Data Source={newDbPath}")
  verifyNewConn.Open()

  use statusCmd =
    new SqliteCommand("SELECT status FROM _migration_status WHERE id = 0", verifyNewConn)

  let newStatus = statusCmd.ExecuteScalar() |> string
  Assert.Equal("migrating", newStatus)

  use accountCountCmd =
    new SqliteCommand("SELECT COUNT(*) FROM account", verifyNewConn)

  let accountCount = accountCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(1L, accountCount)

  use invoiceCountCmd =
    new SqliteCommand("SELECT COUNT(*) FROM invoice", verifyNewConn)

  let invoiceCount = invoiceCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(1L, invoiceCount)

  use mappingCountCmd =
    new SqliteCommand("SELECT COUNT(*) FROM _id_mapping", verifyNewConn)

  let mappingCount = mappingCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(2L, mappingCount)

  use progressCmd =
    new SqliteCommand(
      "SELECT last_replayed_log_id, drain_completed FROM _migration_progress WHERE id = 0",
      verifyNewConn
    )

  use progressReader = progressCmd.ExecuteReader()
  Assert.True(progressReader.Read())
  Assert.Equal(0L, progressReader.GetInt64(0))
  Assert.Equal(0L, progressReader.GetInt64(1))
  Assert.False(progressReader.Read())

  use schemaIdentityCmd =
    new SqliteCommand("SELECT schema_hash FROM _schema_identity WHERE id = 0", verifyNewConn)

  let storedSchemaHash = schemaIdentityCmd.ExecuteScalar() |> string
  Assert.Equal(expectedSchemaHash, storedSchemaHash)

  verifyOldConn.Close()
  verifyNewConn.Close()
  setupOldConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``drain replays accumulated log entries and records replay checkpoint`` () =
  let tempDir = Path.Combine(Path.GetTempPath(), $"mig_drain_flow_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let oldDbPath = Path.Combine(tempDir, "old.db")
  let newDbPath = Path.Combine(tempDir, "new.db")
  let schemaPath = Path.Combine(tempDir, "schema.fsx")

  use setupOldConn = new SqliteConnection($"Data Source={oldDbPath}")
  setupOldConn.Open()

  [ "PRAGMA foreign_keys = ON;"
    "CREATE TABLE account(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);"
    "CREATE TABLE invoice(id INTEGER PRIMARY KEY AUTOINCREMENT, account_id INTEGER NOT NULL, total REAL NOT NULL, FOREIGN KEY(account_id) REFERENCES account(id));"
    "INSERT INTO account(id, name) VALUES (10, 'Alice');"
    "INSERT INTO invoice(id, account_id, total) VALUES (100, 10, 42.5);" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupOldConn)
    cmd.ExecuteNonQuery() |> ignore)

  let migLibPath = typeof<AutoIncPKAttribute>.Assembly.Location.Replace("\\", "\\\\")

  let script =
    $"""
#r @"{migLibPath}"

open MigLib.Db

[<AutoIncPK "id">]
type Account = {{ id: int64; name: string }}

[<AutoIncPK "id">]
type Invoice = {{ id: int64; account: Account; total: float }}
"""

  File.WriteAllText(schemaPath, script.Trim())

  let migrateResult = runMigrate oldDbPath schemaPath newDbPath |> fun t -> t.Result

  match migrateResult with
  | Error ex -> failwith $"Expected migrate to succeed before drain test, got {ex.Message}"
  | Ok _ -> ()

  [ "INSERT INTO account(id, name) VALUES (11, 'Bob');"
    "INSERT INTO invoice(id, account_id, total) VALUES (101, 11, 15.0);"
    "UPDATE invoice SET total = 99.0 WHERE id = 100;"
    "INSERT INTO _migration_log(txn_id, ordering, operation, table_name, row_data) VALUES (1, 1, 'insert', 'account', '{\"id\":11,\"name\":\"Bob\"}');"
    "INSERT INTO _migration_log(txn_id, ordering, operation, table_name, row_data) VALUES (1, 2, 'insert', 'invoice', '{\"id\":101,\"account_id\":11,\"total\":15.0}');"
    "INSERT INTO _migration_log(txn_id, ordering, operation, table_name, row_data) VALUES (2, 1, 'update', 'invoice', '{\"id\":100,\"account_id\":10,\"total\":99.0}');" ]
  |> List.iter (fun sql ->
    use cmd = new SqliteCommand(sql, setupOldConn)
    cmd.ExecuteNonQuery() |> ignore)

  let drainResult = runDrain oldDbPath newDbPath |> fun t -> t.Result

  match drainResult with
  | Error ex -> failwith $"Expected drain to succeed, got {ex.Message}"
  | Ok result ->
    Assert.Equal(3, result.replayedEntries)
    Assert.Equal(0L, result.remainingEntries)

  use verifyOldConn = new SqliteConnection($"Data Source={oldDbPath}")
  verifyOldConn.Open()

  use markerCmd =
    new SqliteCommand("SELECT status FROM _migration_marker WHERE id = 0", verifyOldConn)

  let markerStatus = markerCmd.ExecuteScalar() |> string
  Assert.Equal("draining", markerStatus)

  use oldLogCountCmd =
    new SqliteCommand("SELECT COUNT(*) FROM _migration_log", verifyOldConn)

  let oldLogCount = oldLogCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(3L, oldLogCount)

  use verifyNewConn = new SqliteConnection($"Data Source={newDbPath}")
  verifyNewConn.Open()

  use accountCountCmd =
    new SqliteCommand("SELECT COUNT(*) FROM account", verifyNewConn)

  let accountCount = accountCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(2L, accountCount)

  use invoiceCountCmd =
    new SqliteCommand("SELECT COUNT(*) FROM invoice", verifyNewConn)

  let invoiceCount = invoiceCountCmd.ExecuteScalar() |> unbox<int64>
  Assert.Equal(2L, invoiceCount)

  use summaryCmd =
    new SqliteCommand(
      "SELECT a.name, i.total FROM invoice i JOIN account a ON i.account_id = a.id ORDER BY a.name",
      verifyNewConn
    )

  use summaryReader = summaryCmd.ExecuteReader()
  Assert.True(summaryReader.Read())
  Assert.Equal("Alice", summaryReader.GetString(0))
  Assert.True(Math.Abs(summaryReader.GetDouble(1) - 99.0) < 0.0001)

  Assert.True(summaryReader.Read())
  Assert.Equal("Bob", summaryReader.GetString(0))
  Assert.True(Math.Abs(summaryReader.GetDouble(1) - 15.0) < 0.0001)
  Assert.False(summaryReader.Read())

  use progressCmd =
    new SqliteCommand(
      "SELECT last_replayed_log_id, drain_completed FROM _migration_progress WHERE id = 0",
      verifyNewConn
    )

  use progressReader = progressCmd.ExecuteReader()
  Assert.True(progressReader.Read())
  Assert.Equal(3L, progressReader.GetInt64(0))
  Assert.Equal(1L, progressReader.GetInt64(1))
  Assert.False(progressReader.Read())

  verifyOldConn.Close()
  verifyNewConn.Close()
  setupOldConn.Close()
  Directory.Delete(tempDir, true)

[<Fact>]
let ``schema reflection maps DU optional cases into extension tables`` () =
  let types = [ typeof<ReflectionStudent>; typeof<ReflectionStudentOpt> ]

  match buildSchemaFromTypes types with
  | Error e -> failwith $"reflection failed: {e}"
  | Ok schema ->
    let extension =
      schema.tables
      |> List.find (fun table -> table.name = "reflection_student_email")

    let studentId =
      extension.columns
      |> List.find (fun column -> column.name = "reflection_student_id")

    let hasPk =
      studentId.constraints
      |> List.exists (function
        | PrimaryKey _ -> true
        | _ -> false)

    Assert.True hasPk

    let hasFk =
      studentId.constraints
      |> List.exists (function
        | ForeignKey fk -> fk.refTable = "reflection_student"
        | _ -> false)

    Assert.True hasFk
    Assert.True(extension.columns |> List.exists (fun column -> column.name = "email"))

[<Fact>]
let ``schema reflection maps ViewSql views`` () =
  let types = [ typeof<ReflectionStudentView> ]

  match buildSchemaFromTypes types with
  | Error e -> failwith $"reflection failed: {e}"
  | Ok schema ->
    let view =
      schema.views |> List.find (fun item -> item.name = "reflection_student_view")

    Assert.Equal("SELECT id, name FROM reflection_student", view.sqlTokens |> Seq.head)
    Assert.True(view.queryByAnnotations |> List.exists (fun q -> q.columns = [ "name" ]))

[<Fact>]
let ``schema reflection synthesizes SQL for View with Join attributes`` () =
  let types =
    [ typeof<JoinStudent>
      typeof<JoinCourse>
      typeof<JoinCourseGrade>
      typeof<JoinStudentCourseGrade> ]

  match buildSchemaFromTypes types with
  | Error e -> failwith $"reflection failed: {e}"
  | Ok schema ->
    let view =
      schema.views |> List.find (fun item -> item.name = "join_student_course_grade")

    let sql = view.sqlTokens |> Seq.head

    Assert.Contains("CREATE VIEW join_student_course_grade AS", sql)
    Assert.Contains("FROM join_course jc", sql)
    Assert.Contains("JOIN join_student js ON jc.student_id = js.id", sql)
    Assert.Contains("JOIN join_course_grade jcg ON jcg.course_id = jc.id", sql)
    Assert.Contains("js.id AS student_id", sql)
    Assert.Contains("js.name AS student_name", sql)
    Assert.Contains("jc.title AS title", sql)
    Assert.Contains("jcg.grade AS grade", sql)

[<Fact>]
let ``codegen generates module and query methods from schema model`` () =
  let tempDir = Path.Combine(Path.GetTempPath(), $"mig_codegen_{Guid.NewGuid()}")
  Directory.CreateDirectory tempDir |> ignore

  let outputPath = Path.Combine(tempDir, "Students.fs")

  let studentTable =
    { name = "student"
      columns =
        [ { name = "id"
            columnType = SqlInteger
            constraints =
              [ PrimaryKey
                  { constraintName = None
                    columns = []
                    isAutoincrement = true } ] }
          { name = "name"
            columnType = SqlText
            constraints = [ NotNull ] }
          { name = "age"
            columnType = SqlInteger
            constraints = [ NotNull ] } ]
      constraints = []
      queryByAnnotations = [ { columns = [ "name"; "age" ] } ]
      queryLikeAnnotations = [ { columns = [ "name" ] } ]
      queryByOrCreateAnnotations = [ { columns = [ "name"; "age" ] } ]
      insertOrIgnoreAnnotations = [ InsertOrIgnoreAnnotation ] }

  let schema =
    { emptyFile with
        tables = [ studentTable ] }

  match generateCodeFromModel "Students" schema outputPath with
  | Error e -> failwith $"codegen failed: {e}"
  | Ok _ ->
    let generated = File.ReadAllText outputPath
    Assert.Contains("module Students", generated)
    Assert.Contains("open MigLib.Db", generated)
    Assert.Contains("static member Insert (item: Student) (tx: SqliteTransaction)", generated)
    Assert.Contains("static member SelectAll (tx: SqliteTransaction)", generated)
    Assert.Contains("static member SelectByNameAge (name: string, age: int64) (tx: SqliteTransaction)", generated)
    Assert.Contains("MigrationLog.ensureWriteAllowed tx", generated)
    Assert.Contains("MigrationLog.recordInsert tx \"student\"", generated)
    Assert.Contains("MigrationLog.recordUpdate tx \"student\"", generated)
    Assert.Contains("MigrationLog.recordDelete tx \"student\"", generated)
    Assert.DoesNotContain(": Result<", generated)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``querybyorcreate for regular tables re-queries by annotation columns`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_codegen_querybyorcreate_regular_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let outputPath = Path.Combine(tempDir, "SlugArticle.fs")

  match generateCodeFromTypes "SlugArticleQueries" [ typeof<SlugArticle> ] outputPath with
  | Error e -> failwith $"codegen-from-types failed: {e}"
  | Ok _ ->
    let generated = File.ReadAllText outputPath
    Assert.Contains("static member SelectBySlugOrInsert (newItem: SlugArticle) (tx: SqliteTransaction)", generated)
    Assert.Contains("SELECT slug, title FROM slug_article WHERE slug = @slug LIMIT 1", generated)
    Assert.DoesNotContain("let! getResult = SlugArticle.SelectById", generated)
    Assert.Contains("| Ok _ ->", generated)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``querybyorcreate for normalized tables uses base PK column in re-query joins`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_codegen_querybyorcreate_normalized_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let outputPath = Path.Combine(tempDir, "Product.fs")

  match generateCodeFromTypes "ProductQueries" [ typeof<Product>; typeof<ProductOpt> ] outputPath with
  | Error e -> failwith $"codegen-from-types failed: {e}"
  | Ok _ ->
    let generated = File.ReadAllText outputPath
    Assert.Contains("static member SelectByNameOrInsert", generated)
    Assert.Contains("(newItem: NewProduct)", generated)
    Assert.Contains("LEFT JOIN product_stock estock ON b.code = estock.product_id", generated)
    Assert.Contains("LEFT JOIN product_stock ext0 ON product.code = ext0.product_id", generated)
    Assert.DoesNotContain("LEFT JOIN product_stock ext0 ON product.id = ext0.product_id", generated)
    Assert.DoesNotContain("cmd2.Parameters.AddWithValue(\"@product_id\", productId)", generated)
    Assert.DoesNotContain("let! getResult = Product.SelectById newId tx", generated)
    Assert.Contains("| Ok _ ->", generated)
    Assert.DoesNotContain(": Result<", generated)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``schema reflection rejects type with both AutoIncPK and PK`` () =
  match buildSchemaFromTypes [ typeof<ConflictingPkStudent> ] with
  | Ok _ -> failwith "Expected reflection failure for conflicting PK attributes"
  | Error error ->
    Assert.Contains("has both AutoIncPK and PK attributes", error)
    Assert.Contains("ConflictingPkStudent", error)

[<Fact>]
let ``schema reflection rejects record fields referencing types outside schema`` () =
  match buildSchemaFromTypes [ typeof<WalletWithOutsideRef> ] with
  | Ok _ -> failwith "Expected reflection failure for outside-schema reference"
  | Error error ->
    Assert.Contains("WalletWithOutsideRef.account", error)
    Assert.Contains("outside schema", error)

[<Fact>]
let ``schema reflection rejects foreign-key references to types without primary keys`` () =
  match buildSchemaFromTypes [ typeof<ParentWithoutPk>; typeof<ChildWithParentWithoutPk> ] with
  | Ok _ -> failwith "Expected reflection failure for FK target without PK"
  | Error error ->
    Assert.Contains("ChildWithParentWithoutPk.parent", error)
    Assert.Contains("does not declare PK or AutoIncPK", error)

[<Fact>]
let ``schema reflection rejects disconnected view join chains`` () =
  let types =
    [ typeof<JoinChainA>
      typeof<JoinChainB>
      typeof<JoinChainC>
      typeof<JoinChainD>
      typeof<DisconnectedJoinView> ]

  match buildSchemaFromTypes types with
  | Ok _ -> failwith "Expected reflection failure for disconnected Join chain"
  | Error error ->
    Assert.Contains("Join chain is disconnected", error)
    Assert.Contains("join_chain_c", error)

[<Fact>]
let ``querybyorinsert works for composite primary keys without SelectById fallback`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_codegen_querybyorcreate_composite_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let outputPath = Path.Combine(tempDir, "OrderItem.fs")

  let orderItemTable =
    { name = "order_item"
      columns =
        [ { name = "order_id"
            columnType = SqlInteger
            constraints = [ NotNull ] }
          { name = "sku"
            columnType = SqlText
            constraints = [ NotNull ] }
          { name = "description"
            columnType = SqlText
            constraints = [ NotNull ] } ]
      constraints =
        [ PrimaryKey
            { constraintName = None
              columns = [ "order_id"; "sku" ]
              isAutoincrement = false } ]
      queryByAnnotations = []
      queryLikeAnnotations = []
      queryByOrCreateAnnotations = [ { columns = [ "description" ] } ]
      insertOrIgnoreAnnotations = [] }

  let schema =
    { emptyFile with
        tables = [ orderItemTable ] }

  match generateCodeFromModel "OrderItemQueries" schema outputPath with
  | Error error -> failwith $"codegen failed: {error}"
  | Ok _ ->
    let generated = File.ReadAllText outputPath
    Assert.Contains("static member SelectByDescriptionOrInsert (newItem: OrderItem) (tx: SqliteTransaction)", generated)

    Assert.Contains(
      "SELECT order_id, sku, description FROM order_item WHERE description = @description LIMIT 1",
      generated
    )

    Assert.DoesNotContain("let! getResult = OrderItem.SelectById", generated)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``codegen writes CPM project references`` () =
  let tempDir = Path.Combine(Path.GetTempPath(), $"mig_proj_{Guid.NewGuid()}")
  Directory.CreateDirectory tempDir |> ignore

  let projectPath = writeGeneratedProjectFile tempDir "students" [ "Students.fs" ]
  let generatedProject = File.ReadAllText projectPath

  Assert.Contains("<PackageReference Include=\"FsToolkit.ErrorHandling\" />", generatedProject)
  Assert.Contains("<PackageReference Include=\"Microsoft.Data.Sqlite\" />", generatedProject)
  Assert.Contains("<PackageReference Include=\"MigLib\" />", generatedProject)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``codegen can run directly from reflected types`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_codegen_types_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let outputPath = Path.Combine(tempDir, "ReflectionStudent.fs")

  match generateCodeFromTypes "ReflectionStudent" [ typeof<ReflectionStudent> ] outputPath with
  | Error e -> failwith $"codegen-from-types failed: {e}"
  | Ok _ ->
    let generated = File.ReadAllText outputPath
    Assert.Contains("module ReflectionStudent", generated)
    Assert.Contains("static member SelectByNameAge (name: string, age: int64) (tx: SqliteTransaction)", generated)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``schema script evaluation loads fsx types and seed inserts`` () =
  let tempDir = Path.Combine(Path.GetTempPath(), $"mig_script_{Guid.NewGuid()}")
  Directory.CreateDirectory tempDir |> ignore

  let scriptPath = Path.Combine(tempDir, "schema.fsx")
  let migLibPath = typeof<AutoIncPKAttribute>.Assembly.Location.Replace("\\", "\\\\")

  let script =
    $"""
#r @"{migLibPath}"

open MigLib.Db

[<AutoIncPK "id">]
type Role = {{ id: int64; name: string }}

[<AutoIncPK "id">]
type Student = {{ id: int64; role: Role; name: string }}

let roles = [
  {{ id = 1L; name = "admin" }}
  {{ id = 2L; name = "student" }}
]

let defaultStudent = {{ id = 10L; role = {{ id = 1L; name = "admin" }}; name = "System" }}
"""

  File.WriteAllText(scriptPath, script.Trim())

  match buildSchemaFromScript scriptPath with
  | Error e -> failwith $"schema script failed: {e}"
  | Ok schema ->
    Assert.True(schema.tables |> List.exists (fun table -> table.name = "role"))
    Assert.True(schema.tables |> List.exists (fun table -> table.name = "student"))

    let inserts = schema.inserts
    Assert.Equal(2, inserts.Length)
    Assert.Equal("role", inserts.[0].table)
    Assert.Equal("student", inserts.[1].table)
    Assert.Equal(2, inserts.[0].values.Length)
    Assert.Single inserts.[1].values |> ignore

    let studentInsert = inserts.[1]
    let row = studentInsert.values.Head

    let roleIdIndex =
      studentInsert.columns |> List.findIndex (fun name -> name = "role_id")

    match row.[roleIdIndex] with
    | Integer value -> Assert.Equal(1, value)
    | Value value -> Assert.Equal("1", value)
    | other -> failwith $"Unexpected role_id expression: {other}"

  Directory.Delete(tempDir, true)

[<Fact>]
let ``codegen can run directly from fsx schema script`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_codegen_script_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let scriptPath = Path.Combine(tempDir, "schema.fsx")
  let outputPath = Path.Combine(tempDir, "Generated.fs")
  let migLibPath = typeof<AutoIncPKAttribute>.Assembly.Location.Replace("\\", "\\\\")

  let script =
    $"""
#r @"{migLibPath}"

open MigLib.Db

[<AutoIncPK "id">]
[<SelectBy "name">]
type Student = {{ id: int64; name: string }}
"""

  File.WriteAllText(scriptPath, script.Trim())

  match generateCodeFromScript "Generated" scriptPath outputPath with
  | Error e -> failwith $"codegen-from-script failed: {e}"
  | Ok _ ->
    let generated = File.ReadAllText outputPath
    Assert.Contains("module Generated", generated)
    Assert.Contains("static member SelectByName (name: string) (tx: SqliteTransaction)", generated)

  Directory.Delete(tempDir, true)

[<Fact>]
let ``schema script evaluation reports syntax errors`` () =
  let tempDir =
    Path.Combine(Path.GetTempPath(), $"mig_script_syntax_error_{Guid.NewGuid()}")

  Directory.CreateDirectory tempDir |> ignore

  let scriptPath = Path.Combine(tempDir, "broken.fsx")
  File.WriteAllText(scriptPath, "let broken =")

  match buildSchemaFromScript scriptPath with
  | Ok _ -> failwith "Expected schema script evaluation to fail on syntax error"
  | Error error ->
    Assert.Contains("Failed to evaluate script", error)
    Assert.Contains("error FS", error)

  Directory.Delete(tempDir, true)
