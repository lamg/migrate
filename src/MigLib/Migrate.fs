namespace MigLib

module Migrate =
  open System
  open System.IO
  open System.Reflection
  open System.Threading.Tasks
  open DbUp
  open MigLib.Sqlite

  let private ensureParentDirectory (dbPath: string) =
    let directory = Path.GetDirectoryName(Path.GetFullPath dbPath)

    if not (String.IsNullOrWhiteSpace directory) then
      Directory.CreateDirectory directory |> ignore

  let private connectionString dbPath = publicConnectionString dbPath

  let private runUpgrade (build: unit -> DbUp.Engine.UpgradeEngine) : Task<Result<unit, string>> =
    task {
      try
        // Warm SQLitePCL / create empty file if needed via Microsoft.Data.Sqlite first.
        let result = build().PerformUpgrade()

        if result.Successful then
          return Ok()
        else if isNull result.Error then
          return Error "migration failed"
        else
          return Error result.Error.Message
      with ex ->
        return Error ex.Message
    }

  /// Applies embedded DbUp scripts from <paramref name="assembly"/> to the database at <paramref name="dbPath"/>.
  let migrateEmbedded (dbPath: string) (assembly: Assembly) : Task<Result<unit, string>> =
    task {
      try
        ensureParentDirectory dbPath
        let cs = connectionString dbPath
        use _warmup = openConnection dbPath

        return!
          runUpgrade (fun () ->
            DeployChanges.To
              .SqliteDatabase(cs)
              .WithScriptsEmbeddedInAssembly(assembly)
              .LogToNowhere()
              .Build())
      with ex ->
        return Error ex.Message
    }

  /// Applies ordered *.sql scripts from <paramref name="scriptsDirectory"/> to the database at <paramref name="dbPath"/>.
  let migrateScripts (dbPath: string) (scriptsDirectory: string) : Task<Result<unit, string>> =
    task {
      try
        if not (Directory.Exists scriptsDirectory) then
          return Error $"migrations directory not found: {scriptsDirectory}"
        else
          ensureParentDirectory dbPath
          let cs = connectionString dbPath
          use _warmup = openConnection dbPath

          return!
            runUpgrade (fun () ->
              DeployChanges.To
                .SqliteDatabase(cs)
                .WithScriptsFromFileSystem(scriptsDirectory)
                .LogToNowhere()
                .Build())
      with ex ->
        return Error ex.Message
    }

  /// Same as <see cref="migrateScripts"/> but accepts a connection string (for advanced callers).
  let migrateScriptsWithConnectionString (cs: string) (scriptsDirectory: string) : Task<Result<unit, string>> =
    task {
      try
        if not (Directory.Exists scriptsDirectory) then
          return Error $"migrations directory not found: {scriptsDirectory}"
        else
          ensureInitialized ()

          return!
            runUpgrade (fun () ->
              DeployChanges.To
                .SqliteDatabase(cs)
                .WithScriptsFromFileSystem(scriptsDirectory)
                .LogToNowhere()
                .Build())
      with ex ->
        return Error ex.Message
    }

