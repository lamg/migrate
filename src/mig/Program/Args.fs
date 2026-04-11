namespace Mig

open Argu

module ProgramArgs =
  [<CliPrefix(CliPrefix.DoubleDash)>]
  type MigrateArgs =
    | [<AltCommandLine("-d")>] Dir of path: string
    | [<AltCommandLine("-a")>] Assembly of path: string
    | [<AltCommandLine("-m")>] Module of name: string
    | [<AltCommandLine("-i")>] Instance of name: string

    interface IArgParserTemplate with
      member this.Usage =
        match this with
        | Dir _ -> "directory that contains existing sqlite files for the migration (default: current directory)"
        | Assembly _ -> "compiled assembly that contains a generated Db-style module"
        | Module _ -> "compiled module name when using --assembly (default: Db)"
        | Instance _ -> "database instance name used in <app>-<instance>-<hash>.sqlite (default: main)"

  [<CliPrefix(CliPrefix.DoubleDash)>]
  type OfflineArgs =
    | [<AltCommandLine("-d")>] Dir of path: string
    | [<AltCommandLine("-a")>] Assembly of path: string
    | [<AltCommandLine("-m")>] Module of name: string
    | [<AltCommandLine("-i")>] Instance of name: string

    interface IArgParserTemplate with
      member this.Usage =
        match this with
        | Dir _ -> "directory that contains existing sqlite files for the migration (default: current directory)"
        | Assembly _ -> "compiled assembly that contains a generated Db-style module"
        | Module _ -> "compiled module name when using --assembly (default: Db)"
        | Instance _ -> "database instance name used in <app>-<instance>-<hash>.sqlite (default: main)"

  [<CliPrefix(CliPrefix.DoubleDash)>]
  type InitArgs =
    | [<AltCommandLine("-d")>] Dir of path: string
    | [<AltCommandLine("-a")>] Assembly of path: string
    | [<AltCommandLine("-m")>] Module of name: string
    | [<AltCommandLine("-i")>] Instance of name: string

    interface IArgParserTemplate with
      member this.Usage =
        match this with
        | Dir _ ->
          "directory that contains the target sqlite location for the generated Db module (default: current directory)"
        | Assembly _ -> "compiled assembly that contains a generated Db-style module"
        | Module _ -> "compiled module name when using --assembly (default: Db)"
        | Instance _ -> "database instance name used in <app>-<instance>-<hash>.sqlite (default: main)"

  [<CliPrefix(CliPrefix.DoubleDash)>]
  type PlanArgs =
    | [<AltCommandLine("-d")>] Dir of path: string
    | [<AltCommandLine("-a")>] Assembly of path: string
    | [<AltCommandLine("-m")>] Module of name: string
    | [<AltCommandLine("-i")>] Instance of name: string

    interface IArgParserTemplate with
      member this.Usage =
        match this with
        | Dir _ -> "directory that contains existing sqlite files for the migration (default: current directory)"
        | Assembly _ -> "compiled assembly that contains a generated Db-style module"
        | Module _ -> "compiled module name when using --assembly (default: Db)"
        | Instance _ -> "database instance name used in <app>-<instance>-<hash>.sqlite (default: main)"

  [<CliPrefix(CliPrefix.DoubleDash)>]
  type DrainArgs =
    | [<AltCommandLine("-d")>] Dir of path: string
    | [<AltCommandLine("-a")>] Assembly of path: string
    | [<AltCommandLine("-m")>] Module of name: string
    | [<AltCommandLine("-i")>] Instance of name: string

    interface IArgParserTemplate with
      member this.Usage =
        match this with
        | Dir _ -> "directory that contains Schema.fs and <dir>-<hash>.sqlite files (default: current directory)"
        | Assembly _ -> "compiled assembly that contains a generated Db-style module"
        | Module _ -> "compiled module name when using --assembly (default: Db)"
        | Instance _ -> "database instance name used in <app>-<instance>-<hash>.sqlite (default: main)"

  [<CliPrefix(CliPrefix.DoubleDash)>]
  type CutoverArgs =
    | [<AltCommandLine("-d")>] Dir of path: string
    | [<AltCommandLine("-a")>] Assembly of path: string
    | [<AltCommandLine("-m")>] Module of name: string
    | [<AltCommandLine("-i")>] Instance of name: string

    interface IArgParserTemplate with
      member this.Usage =
        match this with
        | Dir _ -> "directory that contains Schema.fs and <dir>-<hash>.sqlite files (default: current directory)"
        | Assembly _ -> "compiled assembly that contains a generated Db-style module"
        | Module _ -> "compiled module name when using --assembly (default: Db)"
        | Instance _ -> "database instance name used in <app>-<instance>-<hash>.sqlite (default: main)"

  [<CliPrefix(CliPrefix.DoubleDash)>]
  type ArchiveOldArgs =
    | [<AltCommandLine("-d")>] Dir of path: string
    | [<AltCommandLine("-a")>] Assembly of path: string
    | [<AltCommandLine("-m")>] Module of name: string
    | [<AltCommandLine("-i")>] Instance of name: string

    interface IArgParserTemplate with
      member this.Usage =
        match this with
        | Dir _ -> "directory that contains Schema.fs and <dir>-<hash>.sqlite files (default: current directory)"
        | Assembly _ -> "compiled assembly that contains a generated Db-style module"
        | Module _ -> "compiled module name when using --assembly (default: Db)"
        | Instance _ -> "database instance name used in <app>-<instance>-<hash>.sqlite (default: main)"

  [<CliPrefix(CliPrefix.DoubleDash)>]
  type ResetArgs =
    | [<AltCommandLine("-d")>] Dir of path: string
    | [<AltCommandLine("-a")>] Assembly of path: string
    | [<AltCommandLine("-m")>] Module of name: string
    | [<AltCommandLine("-i")>] Instance of name: string
    | Dry_Run

    interface IArgParserTemplate with
      member this.Usage =
        match this with
        | Dir _ -> "directory that contains Schema.fs and <dir>-<hash>.sqlite files (default: current directory)"
        | Assembly _ -> "compiled assembly that contains a generated Db-style module"
        | Module _ -> "compiled module name when using --assembly (default: Db)"
        | Instance _ -> "database instance name used in <app>-<instance>-<hash>.sqlite (default: main)"
        | Dry_Run -> "print reset impact without dropping old migration tables or deleting the new database"

  [<CliPrefix(CliPrefix.DoubleDash)>]
  type StatusArgs =
    | [<AltCommandLine("-d")>] Dir of path: string
    | [<AltCommandLine("-a")>] Assembly of path: string
    | [<AltCommandLine("-m")>] Module of name: string
    | [<AltCommandLine("-i")>] Instance of name: string

    interface IArgParserTemplate with
      member this.Usage =
        match this with
        | Dir _ -> "directory that contains Schema.fs and <dir>-<hash>.sqlite files (default: current directory)"
        | Assembly _ -> "compiled assembly that contains a generated Db-style module"
        | Module _ -> "compiled module name when using --assembly (default: Db)"
        | Instance _ -> "database instance name used in <app>-<instance>-<hash>.sqlite (default: main)"

  [<CliPrefix(CliPrefix.DoubleDash)>]
  type CodegenArgs =
    | [<AltCommandLine("-d")>] Dir of path: string
    | [<AltCommandLine("-a")>] Assembly of path: string
    | [<AltCommandLine("-s")>] Schema_Module of name: string
    | [<AltCommandLine("-m")>] Module of name: string
    | [<AltCommandLine("-o")>] Output of path: string

    interface IArgParserTemplate with
      member this.Usage =
        match this with
        | Dir _ -> "directory that contains Schema.fs and generated source files (default: current directory)"
        | Assembly _ -> "compiled assembly that contains schema types for generated Db.fs code"
        | Schema_Module _ -> "compiled schema module name when using --assembly (default: Schema)"
        | Module _ -> "module name for generated F# code (default: Db)"
        | Output _ -> "output file name in the schema directory (default: Db.fs)"

  type Command =
    | [<AltCommandLine("-v")>] Version
    | [<CliPrefix(CliPrefix.None)>] Init of ParseResults<InitArgs>
    | [<CliPrefix(CliPrefix.None)>] Codegen of ParseResults<CodegenArgs>
    | [<CliPrefix(CliPrefix.None)>] Migrate of ParseResults<MigrateArgs>
    | [<CliPrefix(CliPrefix.None)>] Offline of ParseResults<OfflineArgs>
    | [<CliPrefix(CliPrefix.None)>] Plan of ParseResults<PlanArgs>
    | [<CliPrefix(CliPrefix.None)>] Drain of ParseResults<DrainArgs>
    | [<CliPrefix(CliPrefix.None)>] Cutover of ParseResults<CutoverArgs>
    | [<CliPrefix(CliPrefix.None); CustomCommandLine("archive-old")>] ArchiveOld of ParseResults<ArchiveOldArgs>
    | [<CliPrefix(CliPrefix.None)>] Reset of ParseResults<ResetArgs>
    | [<CliPrefix(CliPrefix.None)>] Status of ParseResults<StatusArgs>

    interface IArgParserTemplate with
      member this.Usage =
        match this with
        | Version -> "print mig version"
        | Init _ -> "initialize a schema-matched database from a compiled generated module"
        | Codegen _ -> "generate Db.fs and query helpers from Schema.fs plus compiled schema types"
        | Migrate _ -> "create new database and copy data from old using a compiled generated module"
        | Offline _ -> "run a one-shot offline migration using a compiled generated module"
        | Plan _ -> "show a dry-run migration plan using a compiled generated module"
        | Drain _ -> "stop writes on old database and replay accumulated changes"
        | Cutover _ -> "mark new database as ready for serving"
        | ArchiveOld _ -> "archive the old database after cutover"
        | Reset _ -> "reset failed migration artifacts on old/new databases"
        | Status _ -> "show current migration state"
