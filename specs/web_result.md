# Web result CE

`MigLib.Web` adds a request-oriented computation expression, `webResult`, for handlers that need a SQLite transaction plus deferred HTTP response mutation.
It is designed for ASP.NET Core services that already use MigLib's `TxnStep`, generated query helpers, `txn`, and `DbRuntime`.

## Goals

- Execute request database work inside one SQLite transaction.
- Reuse `TxnStep<'a>` values produced by generated query helpers and custom `txn` helpers.
- Keep application errors distinct from database failures.
- Make `HttpContext` access explicit and easy to test.
- Defer response mutation until after a successful commit.
- Support pluggable post-commit response effects.
- Keep time access abstract through `IClock`.

## Non-goals

- Routing, model binding, authentication, or middleware composition.
- A host abstraction beyond ASP.NET Core `HttpContext`.
- Distributed guarantees for side effects that run after commit.
- Automatic wrapping of arbitrary .NET exceptions into `WebError`.

## Core model

| Type | Meaning |
|---|---|
| `WebError<'appError>` | `DbError of SqliteException | MissingHttpContext | AppError of 'appError` |
| `WebCtx<'env, 'custom>` | Current environment, active `SqliteTransaction`, optional `HttpContext`, and queued response effects |
| `WebOp<'env, 'appError, 'custom, 'a>` | Request operation: `WebCtx<'env, 'custom> -> Task<Result<'a, WebError<'appError>>>` |
| `ResponseEffect<'custom>` | Deferred response step: status, headers, body, JSON, redirect, cookies, or a custom effect |
| `WebRuntime<'env, 'appError, 'custom>` | Runtime environment, default JSON options, and custom-effect interpreter |
| `IClock` | Injectable time source for request logic |
| `IHasDbRuntime` | Environment contract from `MigLib.Db` required by `run` and `runSimple` |

`MigLib.Db` exposes `DbRuntime.RunInTransaction` so `webResult` can reuse the same transaction machinery as `dbTxn` while mapping database failures into `WebError.DbError`.

## Execution model

1. The caller builds a runtime with `WebRuntime.create` or uses `runSimple`.
2. `run` allocates an empty in-memory response plan.
3. `run` opens a transaction through `runtime.Env.DbRuntime.RunInTransaction DbError`.
4. The `webResult` operation runs with a `WebCtx` containing `env`, `tx`, optional `httpContext`, and the shared response plan.
5. `Respond.*` helpers append `ResponseEffect` values to the plan. They do not mutate `HttpContext.Response` inline.
6. If the operation returns `Error _`, the transaction rolls back and the queued response plan is discarded.
7. If the operation returns `Ok value`, the transaction commits first.
8. After commit, queued response effects are applied in insertion order to `HttpContext.Response`.
9. `run` returns the original `Ok value` or the first `WebError` produced during operation execution or custom-effect application.

Important details:

- `Respond.*` and `Web.httpContext` require `Some HttpContext`. Without one they return `MissingHttpContext` during operation execution, which causes rollback.
- An operation that never touches `HttpContext` can run with `None`.
- Response effects are sequenced after commit. This prevents partially-written HTTP responses from being observed for rolled-back transactions.

## Error and commit semantics

| Source | Result from `run` | Database state | Response effects |
|---|---|---|---|
| `TxnStep` error or `SqliteException` during DB work | `Error (DbError ex)` | Rolled back | Discarded |
| `TxnStep<Result<'a, 'appError>>` inner error | `Error (AppError appError)` | Rolled back | Discarded |
| `Result<'a, 'appError>` / `Task<Result<'a, 'appError>>` error | `Error (AppError appError)` | Rolled back | Discarded |
| Explicit `WebError` (`Web.failWeb`, `Result<_, WebError<_>>`, etc.) | That `WebError` | Rolled back | Discarded |
| `Web.httpContext` or any `Respond.*` helper with no context | `Error MissingHttpContext` | Rolled back | Discarded |
| `Respond.custom` interpreter returns `Error _` after commit | That `WebError` | Already committed | Earlier effects stay applied |

Only `SqliteException`, typed application errors, and explicit `WebError` values participate in the `Result` contract.
Other exceptions from `Task`, response writing, or custom code are not normalized and fault the returned task.

## CE surface

Inside `webResult`, `let!` / `do!` support:

- `WebOp<'env, 'appError, 'custom, 'a>`
- `TxnStep<'a>`
- `Result<'a, 'appError>`
- `Result<'a, WebError<'appError>>`
- `Task<Result<'a, 'appError>>`
- `Task<Result<'a, WebError<'appError>>>`
- `Task<'a>`
- `Task`

The builder also supports standard control-flow members:

- `return`
- `return!` for `WebOp`, `TxnStep`, and the supported `Result` / `Task<Result>` forms
- `Zero`
- `Combine`
- `Delay`
- `TryWith`
- `TryFinally`
- `Using`
- `While`
- `For`

Plain `Task<'a>` and `Task` are bind-only. They are not valid `return!` targets.

## Helper modules

### `Web`

- `env` returns the current environment.
- `tx` returns the current `SqliteTransaction`.
- `tryHttpContext` returns `HttpContext option`.
- `httpContext` returns `HttpContext` or `MissingHttpContext`.
- `fail` converts an application error into `AppError`.
- `failWeb` returns any explicit `WebError`.
- `ignore` discards a successful `WebOp` value while preserving errors and effects.
- `requireSome` converts `option` values into either `Ok value` or `AppError`.
- `ofAppResult` lifts `Result<'a, 'appError>` into `WebOp`.
- `ofAppTaskResult` lifts `Task<Result<'a, 'appError>>` into `WebOp`.
- `ofTxnAppResult` lifts `TxnStep<Result<'a, 'appError>>` into `WebOp`.
- `ofWebResult` lifts `Result<'a, WebError<'appError>>` into `WebOp`.

### `Clock`

`Clock` helpers require `'env :> IClock`:

- `utcNow`
- `utcNowRfc3339`
- `utcNowPlusDaysRfc3339`

These keep handler logic deterministic in tests and avoid direct dependency on `DateTimeOffset.UtcNow`.

### `Respond`

`Respond` queues response effects:

- `statusCode`
- `header`
- `appendHeader`
- `text`
- `html`
- `bytes`
- `json`
- `jsonWith`
- `redirect`
- `permanentRedirect`
- `setCookie`
- `deleteCookie`
- `custom`

Effects are recorded in the order they appear in the CE and applied in the same order after commit.

## Cookies and JSON

`CookieSpec` is MigLib's serializable cookie description:

- `CookieSpec.empty` is the zero-value configuration.
- `CookieSpec.toAspNetCore` maps it to `CookieOptions`.
- Only explicitly provided optional values are copied into `CookieOptions`.

JSON behavior:

- `WebRuntime.create` uses `JsonSerializerDefaults.Web` as the runtime default.
- `WebRuntime.withJsonOptions` overrides the runtime-wide default.
- `Respond.jsonWith` overrides JSON options for one queued write.
- `Respond.json` serializes `null` as JSON `null`.

## Runtime API

Construction:

- `WebRuntime.create env applyCustomEffect`
- `WebRuntime.withJsonOptions jsonOptions runtime`
- `WebRuntime.createSimple env`

Execution:

- `run runtime httpContext operation`
- `runSimple env httpContext operation`

`run` and `runSimple` require `'env :> IHasDbRuntime`.
`MigLib.Web` therefore depends on the same `DbRuntime` that powers `dbTxn`.

## Relationship to `dbTxn` and `txn`

- Use `dbTxn` when the caller only needs a transaction boundary around database work.
- Use `txn` to build reusable transaction-scoped helpers.
- Use `webResult` when the caller also needs typed application errors, environment access, optional `HttpContext`, and deferred response composition.
- Any `TxnStep<'a>` can be bound directly inside `webResult`.
- Use `Web.ofTxnAppResult` when a transaction-scoped helper returns `TxnStep<Result<'a, 'appError>>`.
- Generated query helpers, `txn` helpers, and transaction-scoped validation flows therefore compose without ad-hoc adapters.

## Example

```fsharp
open System
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open MigLib.Db
open MigLib.Web

type Env =
  { dbRuntime: DbRuntime
    fixedNow: DateTimeOffset }

  interface IHasDbRuntime with
    member this.DbRuntime = this.dbRuntime

  interface IClock with
    member this.UtcNow() = this.fixedNow
    member this.UtcNowRfc3339() = this.fixedNow.ToUniversalTime().ToString("O")
    member this.UtcNowPlusDaysRfc3339(days: float) =
      this.fixedNow.AddDays days |> fun value -> value.ToUniversalTime().ToString("O")

let createStudent (name: string) =
  webResult {
    let! createdAt = Clock.utcNowRfc3339

    let! id =
      fun tx ->
        task {
          use cmd =
            new SqliteCommand(
              "INSERT INTO student(name, created_at) VALUES (@name, @created_at)",
              tx.Connection,
              tx
            )

          cmd.Parameters.AddWithValue("@name", name) |> ignore
          cmd.Parameters.AddWithValue("@created_at", createdAt) |> ignore
          let! _ = cmd.ExecuteNonQueryAsync()

          use idCmd = new SqliteCommand("SELECT last_insert_rowid()", tx.Connection, tx)
          let! idObj = idCmd.ExecuteScalarAsync()
          return Ok(idObj |> unbox<int64>)
        }

    do! Respond.statusCode 201
    do! Respond.json {| id = id; createdAt = createdAt |}
    return id
  }
```

In this example:

- database writes and ID lookup happen inside one SQLite transaction
- the `201` status and JSON body are only written after the commit succeeds
- any `DbError`, `AppError`, or `MissingHttpContext` result skips the response write and rolls back the transaction

## Current design constraints

- The hosting model is ASP.NET Core because `HttpContext`, `CookieOptions`, and `SameSiteMode` are part of the public API.
- Post-commit response work is intentionally separate from transactional DB work. A custom effect can therefore fail after the database has committed.
- `webResult` does not try to own the entire web stack. It is a small orchestration layer for request handlers that need MigLib transaction semantics plus response composition.
