module Test.NormalizedIntegrationTest

open System.IO
open Xunit
open FsToolkit.ErrorHandling

open migrate.CodeGen

[<Fact>]
let ``End-to-end code generation with normalized schema`` () =
  // Create a temporary directory for the test
  let tempDir = Path.Combine(Path.GetTempPath(), $"mig_test_{System.Guid.NewGuid()}")
  Directory.CreateDirectory tempDir |> ignore

  try
    // Create a SQL file with normalized schema
    let sqlContent =
      """
      CREATE TABLE student (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        name TEXT NOT NULL
      );

      CREATE TABLE student_address (
        student_id INTEGER PRIMARY KEY REFERENCES student(id),
        address TEXT NOT NULL
      );

      CREATE TABLE teacher (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        name TEXT NOT NULL,
        email TEXT
      );
      """

    let sqlFile = Path.Combine(tempDir, "schema.sql")
    File.WriteAllText(sqlFile, sqlContent)

    // Generate code
    let result = CodeGen.generateCode tempDir

    match result with
    | Ok stats ->
      // Verify statistics
      Assert.Equal(1, stats.NormalizedTables) // student with student_address
      Assert.Equal(1, stats.RegularTables) // teacher (has nullable column)
      Assert.Equal(0, stats.Views)
      Assert.NotEmpty stats.GeneratedFiles

      // Verify files were created (FileMapper capitalizes the module name)
      let fsharpFile = Path.Combine(tempDir, "Schema.fs")
      Assert.True(File.Exists fsharpFile, $"F# file should exist: {fsharpFile}")

      let generatedCode = File.ReadAllText fsharpFile

      // Verify DU types were generated for normalized table
      Assert.Contains("type NewStudent =", generatedCode)
      Assert.Contains("type Student =", generatedCode)
      Assert.Contains("| Base of", generatedCode)
      Assert.Contains("| WithAddress of", generatedCode)

      // Verify record type was generated for regular table
      Assert.Contains("type Teacher =", generatedCode)
      Assert.Contains("Email: string option", generatedCode)

      // Verify query methods were generated
      Assert.Contains("static member Insert", generatedCode)
      Assert.Contains("static member GetAll", generatedCode)
      Assert.Contains("static member Update", generatedCode)
      Assert.Contains("static member Delete", generatedCode)

    | Error e -> Assert.Fail $"Code generation failed: {e}"

  finally
    // Cleanup
    if Directory.Exists tempDir then
      Directory.Delete(tempDir, true)

[<Fact>]
let ``Code generation with mixed normalized and regular tables`` () =
  let tempDir = Path.Combine(Path.GetTempPath(), $"mig_test_{System.Guid.NewGuid()}")
  Directory.CreateDirectory tempDir |> ignore

  try
    let sqlContent =
      """
      CREATE TABLE product (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        name TEXT NOT NULL,
        description TEXT
      );

      CREATE TABLE order_record (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        customer_name TEXT NOT NULL
      );

      CREATE TABLE order_record_shipping (
        order_record_id INTEGER PRIMARY KEY REFERENCES order_record(id),
        address TEXT NOT NULL,
        city TEXT NOT NULL
      );
      """

    let sqlFile = Path.Combine(tempDir, "schema.sql")
    File.WriteAllText(sqlFile, sqlContent)

    let result = CodeGen.generateCode tempDir

    match result with
    | Ok stats ->
      Assert.Equal(1, stats.NormalizedTables) // order_record with order_record_shipping
      Assert.Equal(1, stats.RegularTables) // product (has nullable description)

      let fsharpFile = Path.Combine(tempDir, "Schema.fs")
      let generatedCode = File.ReadAllText fsharpFile

      // Verify both patterns were generated
      Assert.Contains("type NewOrderRecord =", generatedCode)
      Assert.Contains("type OrderRecord =", generatedCode)
      Assert.Contains("type Product =", generatedCode)
      Assert.Contains("Description: string option", generatedCode)

    | Error e -> Assert.Fail $"Code generation failed: {e}"

  finally
    if Directory.Exists tempDir then
      Directory.Delete(tempDir, true)

[<Fact>]
let ``Code generation with multiple extensions`` () =
  let tempDir = Path.Combine(Path.GetTempPath(), $"mig_test_{System.Guid.NewGuid()}")
  Directory.CreateDirectory tempDir |> ignore

  try
    let sqlContent =
      """
      CREATE TABLE user (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        username TEXT NOT NULL
      );

      CREATE TABLE user_profile (
        user_id INTEGER PRIMARY KEY REFERENCES user(id),
        bio TEXT NOT NULL
      );

      CREATE TABLE user_settings (
        user_id INTEGER PRIMARY KEY REFERENCES user(id),
        theme TEXT NOT NULL,
        language TEXT NOT NULL
      );
      """

    let sqlFile = Path.Combine(tempDir, "schema.sql")
    File.WriteAllText(sqlFile, sqlContent)

    let result = CodeGen.generateCode tempDir

    match result with
    | Ok stats ->
      Assert.Equal(1, stats.NormalizedTables) // user with 2 extensions
      Assert.Equal(0, stats.RegularTables)

      let fsharpFile = Path.Combine(tempDir, "Schema.fs")
      let generatedCode = File.ReadAllText fsharpFile

      // Verify all DU cases were generated
      Assert.Contains("type NewUser =", generatedCode)
      Assert.Contains("| Base of", generatedCode)
      Assert.Contains("| WithProfile of", generatedCode)
      Assert.Contains("| WithSettings of", generatedCode)

      // Verify Insert method has pattern matching on all cases
      Assert.Contains("match item with", generatedCode)

    | Error e -> Assert.Fail $"Code generation failed: {e}"

  finally
    if Directory.Exists tempDir then
      Directory.Delete(tempDir, true)

[<Fact>]
let ``Code generation statistics are accurate`` () =
  let tempDir = Path.Combine(Path.GetTempPath(), $"mig_test_{System.Guid.NewGuid()}")
  Directory.CreateDirectory tempDir |> ignore

  try
    // Multiple SQL files to test aggregation
    let sql1 =
      """
      CREATE TABLE student (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        name TEXT NOT NULL
      );

      CREATE TABLE student_address (
        student_id INTEGER PRIMARY KEY REFERENCES student(id),
        address TEXT NOT NULL
      );
      """

    let sql2 =
      """
      CREATE TABLE teacher (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        name TEXT NOT NULL,
        email TEXT
      );

      CREATE TABLE course (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        title TEXT NOT NULL
      );
      """

    File.WriteAllText(Path.Combine(tempDir, "students.sql"), sql1)
    File.WriteAllText(Path.Combine(tempDir, "staff.sql"), sql2)

    let result = CodeGen.generateCode tempDir

    match result with
    | Ok stats ->
      // 1 normalized from students.sql, 2 regular from staff.sql
      Assert.Equal(1, stats.NormalizedTables)
      Assert.Equal(2, stats.RegularTables)
      Assert.Equal(3, stats.GeneratedFiles.Length) // project + 2 F# files

    | Error e -> Assert.Fail $"Code generation failed: {e}"

  finally
    if Directory.Exists tempDir then
      Directory.Delete(tempDir, true)

[<Fact>]
let ``Generated code includes convenience properties`` () =
  let tempDir = Path.Combine(Path.GetTempPath(), $"mig_test_{System.Guid.NewGuid()}")
  Directory.CreateDirectory tempDir |> ignore

  try
    let sqlContent =
      """
      CREATE TABLE student (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        name TEXT NOT NULL
      );

      CREATE TABLE student_address (
        student_id INTEGER PRIMARY KEY REFERENCES student(id),
        address TEXT NOT NULL
      );
      """

    let sqlFile = Path.Combine(tempDir, "schema.sql")
    File.WriteAllText(sqlFile, sqlContent)

    let result = CodeGen.generateCode tempDir

    match result with
    | Ok stats ->
      let fsharpFile = Path.Combine(tempDir, "Schema.fs")
      let generatedCode = File.ReadAllText fsharpFile

      // Verify properties are generated
      Assert.Contains("type Student with", generatedCode)

      // Common properties (in all cases)
      Assert.Contains("member this.Id: int64", generatedCode)
      Assert.Contains("member this.Name: string", generatedCode)

      // Partial properties (only in some cases)
      Assert.Contains("member this.Address: string option", generatedCode)

      // Verify pattern matching for partial property (positional patterns)
      Assert.Contains("| Student.Base _ -> None", generatedCode)
      Assert.Contains("| Student.WithAddress(_, _, address) -> Some address", generatedCode)

    | Error e -> Assert.Fail $"Code generation failed: {e}"

  finally
    if Directory.Exists tempDir then
      Directory.Delete(tempDir, true)
