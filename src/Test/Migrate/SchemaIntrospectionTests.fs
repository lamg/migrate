module Test.Migrate.SchemaIntrospectionTests

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Data.Sqlite

open MigLib.Migrate.Discovery
open MigLib.Migrate.SchemaIntrospection
open MigLib.Schema.Types
open MigLib.Types
open Xunit

let private createTempDir name =
  let path = Path.Combine(Path.GetTempPath(), $"{name}_{Guid.NewGuid()}")

  Directory.CreateDirectory path |> ignore
  path

let private writeFile (path: string) (text: string) =
  let directory = Path.GetDirectoryName path

  if not (String.IsNullOrWhiteSpace directory) then
    Directory.CreateDirectory directory |> ignore

  File.WriteAllText(path, text)

let private openConnection dbPath =
  SQLitePCL.Batteries_V2.Init()
  let connection = new SqliteConnection($"Data Source={dbPath}")
  connection.Open()
  connection

let private executeSql (connection: SqliteConnection) sql =
  use cmd = new SqliteCommand(sql, connection)
  cmd.ExecuteNonQuery() |> ignore

let private hasConstraint (predicate: ColumnConstraint -> bool) (column: ColumnDef) =
  column.constraints |> List.exists predicate

let private runtimeProjectPath tempDir = Path.Combine(tempDir, "Runtime.fsproj")

let private schemaProjectPath tempDir =
  Path.Combine(tempDir, "MigSchema", "MigSchema.fsproj")

let private runtimeAssemblyPath tempDir =
  let assemblyName =
    Path.GetFileNameWithoutExtension(typeof<TestGenerated.Db.Marker>.Assembly.Location)

  Path.Combine(tempDir, "bin", "Debug", "net10.0", $"{assemblyName}.dll")

let private writeProjectLayout tempDir =
  let fixtureAssembly = typeof<TestGenerated.Db.Marker>.Assembly.Location
  let assemblyName = Path.GetFileNameWithoutExtension fixtureAssembly

  writeFile
    (runtimeProjectPath tempDir)
    $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>TestGenerated</RootNamespace><AssemblyName>{assemblyName}</AssemblyName></PropertyGroup></Project>"

  writeFile
    (schemaProjectPath tempDir)
    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>TestGeneratedSchema</RootNamespace></PropertyGroup></Project>"

  let targetAssemblyPath = runtimeAssemblyPath tempDir
  Directory.CreateDirectory(Path.GetDirectoryName targetAssemblyPath) |> ignore
  File.Copy(fixtureAssembly, targetAssemblyPath, true)

let private makeProject tempDir =
  { dbInstance = TestGenerated.Db.DefaultDbInstance
    dbDir = tempDir
    targetSchema = TestGenerated.Db.Schema
    dbApp = TestGenerated.Db.DbApp
    schemaIdentity = TestGenerated.Db.SchemaIdentity }

[<Fact>]
let ``loadSchemaFromDatabase reads tables columns defaults and foreign keys`` () =
  let tempDir = createTempDir "mig_schema_introspection"

  try
    let dbPath = Path.Combine(tempDir, "source.sqlite")
    use connection = openConnection dbPath

    executeSql connection "PRAGMA foreign_keys = ON;"
    executeSql connection "CREATE TABLE parent(id INTEGER PRIMARY KEY, name TEXT NOT NULL DEFAULT 'unknown');"

    executeSql
      connection
      "CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER NOT NULL REFERENCES parent(id) ON DELETE CASCADE);"

    match loadSchemaFromDatabase connection |> fun task -> task.Result with
    | Ok schema ->
      let parent = schema.tables |> List.find (fun table -> table.name = "parent")
      let child = schema.tables |> List.find (fun table -> table.name = "child")
      let parentId = parent.columns |> List.find (fun column -> column.name = "id")
      let parentName = parent.columns |> List.find (fun column -> column.name = "name")

      let childParentId =
        child.columns |> List.find (fun column -> column.name = "parent_id")

      Assert.Equal(SqlInteger, parentId.columnType)

      Assert.True(
        parentId
        |> hasConstraint (function
          | PrimaryKey _ -> true
          | _ -> false)
      )

      Assert.True(
        parentName
        |> hasConstraint (function
          | NotNull -> true
          | _ -> false)
      )

      Assert.True(
        parentName
        |> hasConstraint (function
          | Default(String "unknown") -> true
          | _ -> false)
      )

      Assert.True(
        childParentId
        |> hasConstraint (function
          | ForeignKey fk -> fk.refTable = "parent" && fk.refColumns = [ "id" ] && fk.onDelete = Some Cascade
          | _ -> false)
      )
    | Error error -> failwith $"Expected schema introspection to succeed, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``findOldSchema returns none when no source database exists`` () =
  let tempDir = createTempDir "mig_find_old_schema_none"

  try
    writeProjectLayout tempDir
    let report _ = Task.FromResult()

    match findOldSchema report (makeProject tempDir) |> fun task -> task.Result with
    | Ok None -> ()
    | Ok(Some schema) -> failwith $"Expected no old schema, got: {schema}"
    | Error error -> failwith $"Expected findOldSchema to succeed, got: {error}"
  finally
    Directory.Delete(tempDir, true)

[<Fact>]
let ``findOldSchema introspects source database when present`` () =
  let tempDir = createTempDir "mig_find_old_schema_some"

  try
    writeProjectLayout tempDir

    let sourceDbPath =
      Path.Combine(tempDir, "generated-fixture-main-fedcba9876543210.sqlite")

    use connection = openConnection sourceDbPath
    executeSql connection "CREATE TABLE source_item(id INTEGER PRIMARY KEY, name TEXT NOT NULL);"
    connection.Close()

    let messages = ResizeArray<string>()

    let report message =
      messages.Add message
      Task.FromResult()

    match findOldSchema report (makeProject tempDir) |> fun task -> task.Result with
    | Ok(Some schema) ->
      Assert.Contains(schema.tables, fun table -> table.name = "source_item")
      Assert.Contains(messages, fun message -> message.Contains "Reading source database schema")
    | Ok None -> failwith "Expected source schema to be found."
    | Error error -> failwith $"Expected findOldSchema to succeed, got: {error}"
  finally
    Directory.Delete(tempDir, true)
