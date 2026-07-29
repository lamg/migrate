module RuntimeTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open MigLib

let private await (t: Task<'a>) = t.GetAwaiter().GetResult()

[<Fact>]
let ``dbTxn commits writes`` () =
  let dbPath = Path.Combine(Path.GetTempPath(), $"mig-rt-{Guid.NewGuid():N}.sqlite")

  try
    match
      await (
        dbTxn dbPath {
          do!
            Query.exec
              "CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT NOT NULL)"
              []
            |> TxnStep.map ignore

          do!
            Query.exec "INSERT INTO t (id, name) VALUES (1, 'x')" []
            |> TxnStep.map ignore

          return! Query.scalar "SELECT COUNT(*) FROM t" []
        }
      )
    with
    | Error e -> Assert.Fail(string e)
    | Ok count -> Assert.Equal(1L, Convert.ToInt64 count)
  finally
    try
      File.Delete dbPath
    with _ ->
      ()

[<Fact>]
let ``readOnly rejects write path via mode`` () =
  let dbPath = Path.Combine(Path.GetTempPath(), $"mig-ro-{Guid.NewGuid():N}.sqlite")

  try
    // Create file first with a write txn
    match
      await (
        dbTxn dbPath {
          do!
            Query.exec "CREATE TABLE t (id INTEGER PRIMARY KEY)" []
            |> TxnStep.map ignore
        }
      )
    with
    | Error e -> Assert.Fail(string e)
    | Ok() ->
      match
        await (
          readOnlyDbTxn dbPath {
            return!
              Query.exec "INSERT INTO t (id) VALUES (1)" []
          }
        )
      with
      | Ok _ -> Assert.Fail "expected readonly failure"
      | Error(MigError.Sqlite _) -> ()
      | Error other -> Assert.Fail(string other)
  finally
    try
      File.Delete dbPath
    with _ ->
      ()
