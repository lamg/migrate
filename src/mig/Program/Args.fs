namespace Mig

open Argu

module ProgramArgs =
  [<CliPrefix(CliPrefix.DoubleDash)>]
  type MigrateArgs =
    | [<AltCommandLine("-d")>] Dir of path: string
    | [<AltCommandLine("-i")>] Instance of name: string

    interface IArgParserTemplate with
      member this.Usage =
        match this with
        | Dir _ ->
          "runtime project directory containing the runtime .fsproj and database files (default: current directory)"
        | Instance _ -> "database instance name used in <app>-<instance>-<hash>.sqlite (default: main)"

  [<CliPrefix(CliPrefix.DoubleDash)>]
  type InitArgs =
    | [<AltCommandLine("-d")>] Dir of path: string
    | [<AltCommandLine("-i")>] Instance of name: string

    interface IArgParserTemplate with
      member this.Usage =
        match this with
        | Dir _ ->
          "runtime project directory containing the runtime .fsproj and target database location (default: current directory)"
        | Instance _ -> "database instance name used in <app>-<instance>-<hash>.sqlite (default: main)"

  [<CliPrefix(CliPrefix.DoubleDash)>]
  type PlanArgs =
    | [<AltCommandLine("-d")>] Dir of path: string
    | [<AltCommandLine("-i")>] Instance of name: string

    interface IArgParserTemplate with
      member this.Usage =
        match this with
        | Dir _ ->
          "runtime project directory containing the runtime .fsproj and database files (default: current directory)"
        | Instance _ -> "database instance name used in <app>-<instance>-<hash>.sqlite (default: main)"

  [<CliPrefix(CliPrefix.DoubleDash)>]
  type ResetArgs =
    | [<AltCommandLine("-d")>] Dir of path: string
    | [<AltCommandLine("-i")>] Instance of name: string

    interface IArgParserTemplate with
      member this.Usage =
        match this with
        | Dir _ ->
          "runtime project directory containing the runtime .fsproj and database files (default: current directory)"
        | Instance _ -> "database instance name used in <app>-<instance>-<hash>.sqlite (default: main)"

  [<CliPrefix(CliPrefix.DoubleDash)>]
  type StatusArgs =
    | [<AltCommandLine("-d")>] Dir of path: string
    | [<AltCommandLine("-i")>] Instance of name: string

    interface IArgParserTemplate with
      member this.Usage =
        match this with
        | Dir _ ->
          "runtime project directory containing the runtime .fsproj and database files (default: current directory)"
        | Instance _ -> "database instance name used in <app>-<instance>-<hash>.sqlite (default: main)"

  [<CliPrefix(CliPrefix.DoubleDash)>]
  type CodegenArgs =
    | [<AltCommandLine("-d")>] Dir of path: string

    interface IArgParserTemplate with
      member this.Usage =
        match this with
        | Dir _ ->
          "runtime project directory containing the runtime and DomainModeling projects (default: current directory)"

  type Command =
    | [<AltCommandLine("-v")>] Version
    | [<CliPrefix(CliPrefix.None)>] Init of ParseResults<InitArgs>
    | [<CliPrefix(CliPrefix.None)>] Codegen of ParseResults<CodegenArgs>
    | [<CliPrefix(CliPrefix.None)>] Migrate of ParseResults<MigrateArgs>
    | [<CliPrefix(CliPrefix.None)>] Plan of ParseResults<PlanArgs>
    | [<CliPrefix(CliPrefix.None)>] Reset of ParseResults<ResetArgs>
    | [<CliPrefix(CliPrefix.None)>] Status of ParseResults<StatusArgs>

    interface IArgParserTemplate with
      member this.Usage =
        match this with
        | Version -> "print mig version"
        | Init _ -> "initialize a schema-matched database from the runtime project convention"
        | Codegen _ -> "generate Db.fs from the runtime project and DomainModeling convention"
        | Migrate _ -> "create a new database, copy data, and archive the old database"
        | Plan _ -> "show a dry-run migration plan from the runtime project convention"
        | Reset _ -> "remove the current database and restore the latest archived database"
        | Status _ -> "show current command migration state"
