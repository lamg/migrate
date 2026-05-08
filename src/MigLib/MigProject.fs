/// Main entry point for applications using MigLib. This module exposes the
/// project-level workflow functions for discovering or resolving a database
/// project, generating code, initializing databases, planning migrations,
/// migrating, checking status, and resetting from archives.
module MigLib.MigProject

open System.Threading.Tasks
open MigLib.TaskResult

type MigError = Types.MigError
type CodegenResult = Types.CodegenResult
type InitResult = Types.InitResult
type PlanResult = Types.PlanResult
type MigrateResult = Types.MigrateResult
type StatusResult = Types.StatusResult
type ResetResult = Types.ResetResult
type ProgReport = Types.ProgReport
type ResolvedProject = Types.ResolvedProject
type ResolvedGeneratedSchemaModule = Types.ResolvedGeneratedSchemaModule

/// Reusable transaction-bound database operation. Generated query helpers and
/// custom reusable database operations use this shape.
type TxnStep<'a> = MigLib.Runtime.TxnStep.TxnStep<'a>

/// Runtime service for repeatedly executing transaction operations against a
/// fixed database path.
type DbRuntime = Types.DbRuntime

/// Interface for values that expose a <see cref="DbRuntime"/>.
type IHasDbRuntime = Types.IHasDbRuntime

/// Computation expression builder for executing transaction operations against
/// a fixed database path.
type DbTxnBuilder = Types.DbTxnBuilder

/// Computation expression builder for composing reusable transaction operations
/// before binding them to a database path.
type TxnBuilder = Types.TxnBuilder

/// Creates a transaction computation expression builder bound to
/// <paramref name="dbPath"/>. Running the builder returns <see cref="MigError"/>
/// for database failures.
let dbTxn dbPath = Types.dbTxn dbPath

/// Creates a reusable database runtime bound to <paramref name="dbPath"/>.
let dbRuntime dbPath = Types.dbRuntime dbPath

/// Shared transaction computation expression builder for composing reusable
/// <see cref="TxnStep{T}"/> values before binding them to a concrete database
/// path.
let txn = Types.txn

/// Computation expression builder for composing synchronous
/// <c>Result&lt;_, _&gt;</c> workflows.
let result = TaskResult.result

/// Computation expression builder for composing asynchronous
/// <c>Task&lt;Result&lt;_, _&gt;&gt;</c> workflows.
let taskResult = TaskResult.taskResult

/// CLI-shaped workflows for code generation, initialization, migration, status,
/// and reset operations.
module Mig =
  /// Generates typed schema and query source files for the database project at
  /// <paramref name="projectDir"/>.
  let codegen (projectDir: string) : Result<CodegenResult, MigError> = Codegen.Execution.codegen projectDir

  /// Discovers a database project by reflecting over the generated schema in
  /// <paramref name="projDir"/> and returns the resolved project configuration.
  /// Use this before code generation, or in any other situation where the
  /// compiled assembly containing a <see cref="Types.ResolvedGeneratedSchemaModule"/>
  /// is not available yet.
  let discover (projDir: string) (instance: string option) (dbDir: string) : Task<Result<ResolvedProject, MigError>> =
    MigLib.Resolution.Projects.discoverProject projDir instance dbDir

  /// Resolves a database project from an already compiled generated schema
  /// module. Use this when the caller already has access to the compiled
  /// assembly value produced by generated code.
  let resolveFromGeneratedSchema
    (dbDir: string)
    (instance: string option)
    (generatedSchema: ResolvedGeneratedSchemaModule)
    : Task<Result<ResolvedProject, MigError>> =
    MigLib.Resolution.Projects.resolveProjectFromGeneratedSchema dbDir instance generatedSchema

  /// Initializes a new database for the resolved project and applies seed data.
  let init (project: ResolvedProject) : Task<Result<InitResult, MigError>> = Init.Execution.init project

  /// Migrates the resolved project to its target schema, optionally copying data
  /// from the current database and archiving the previous database.
  let migrate (reportProgress: ProgReport) (project: ResolvedProject) : Task<Result<MigrateResult, MigError>> =
    Migrate.Execution.migrate reportProgress project

  /// Builds a migration plan describing whether the current database can
  /// migrate to the resolved target schema.
  let plan (project: ResolvedProject) : Task<Result<PlanResult, MigError>> = Plan.Reporting.plan project

  /// Reports whether the current database exists and needs migration.
  let status (project: ResolvedProject) : Task<Result<StatusResult, MigError>> = Status.Execution.status project

  /// Removes the current database and restores the latest archived database when
  /// one is available.
  let reset (project: ResolvedProject) : Task<Result<ResetResult, MigError>> = Reset.Execution.reset project
