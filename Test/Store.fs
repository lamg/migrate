module Store

open Microsoft.Data.Sqlite
open Hedgehog
open Hedgehog.Xunit
open Migrate
open Migrate.Types
open Migrate.Execution.Store

let genStrPrefix (prefix: string) =
  gen {
    let! str = Gen.string (Range.constant 1 10) Gen.alpha
    return prefix + "_" + str
  }

let genChange: Gen<Diff> =
  gen {
    let! src = Gen.string (Range.linear 1 100) Gen.alphaNum
    let! dest = Gen.string (Range.linear 1 100) Gen.alphaNum
    return Changed(src, dest)
  }

let genAdded: Gen<Diff> =
  gen {
    let! dest = Gen.string (Range.linear 1 100) Gen.alphaNum
    return Added dest
  }

let genRemoved: Gen<Diff> =
  gen {
    let! dest = Gen.string (Range.linear 1 100) Gen.alphaNum
    return Removed dest
  }

let genProposalResult: Gen<ProposalResult> =
  gen {
    let! reason = Gen.choice [ genChange; genAdded; genRemoved ]
    let! statements = Gen.list (Range.linear 1 100) (genStrPrefix "statement")
    let! error = genStrPrefix "error" |> Gen.option

    return
      { reason = reason
        statements = statements
        error = error }
  }

let genVersion: Gen<string> =
  gen {
    let! major = Gen.int32 (Range.linear 0 100)
    let! minor = Gen.int32 (Range.linear 0 100)
    let! patch = Gen.int32 (Range.linear 0 100)
    return $"%d{major}.%d{minor}.%d{patch}"
  }

let genMigrationIntent: Gen<MigrationIntent> =
  gen {
    let! versionRemarks = genStrPrefix "versionRemarks"
    let! steps = Gen.list (Range.linear 1 100) genProposalResult
    let! schemaVersion = genVersion
    let date = Print.nowStr ()

    return
      { versionRemarks = versionRemarks
        steps = steps
        schemaVersion = schemaVersion
        date = date }
  }

let tempDb =
  Execution.Commit.createTempDb
    { tables = []
      indexes = []
      inserts = []
      views = [] }
    "store_insert_test"

let mutable testCount = 0

[<Property>]
let ``Insert migrations`` () =
  use conn = new SqliteConnection($"Data Source=:memory:")

  property {
    conn.Open()
    Init.initStore conn
    let! intent = genMigrationIntent
    use tx = conn.BeginTransaction()
    Insert.storeMigration conn intent
    tx.Commit()
    let x = Get.getMigrations conn |> List.head
    testCount <- testCount + 1
    printf $"\rinsert migration: %d{testCount}"
    return intent.date = x.migration.date
  }

[<Property>]
let ``Amend migrations`` () =
  use conn = new SqliteConnection($"Data Source=:memory:")

  property {
    conn.Open()
    Init.initStore conn
    let! intent = genMigrationIntent
    use tx = conn.BeginTransaction()
    Insert.storeMigration conn intent
    tx.Commit()
    let x = Get.getMigrations conn |> List.head
    let! proposal = Gen.list (Range.linear 1 100) genProposalResult
    testCount <- testCount + 1
    printf $"\ramend migration: %d{testCount}"

    try
      use tx = conn.BeginTransaction()
      Amend.amendLastMigration conn x proposal
      tx.Commit()
      let y = Get.getMigrations conn |> List.head
      return y.steps.Length = x.steps.Length + proposal.Length
    with :? System.AggregateException as e when
        e.Message.Contains
          "'UNIQUE constraint failed: github_com_lamg_migrate_step.migrationId, github_com_lamg_migrate_step.stepIndex'" ->
      return true
  }
