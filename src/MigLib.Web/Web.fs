module MigLib.Web

open System
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Data.Sqlite
open MigLib.Db

type IClock =
  abstract UtcNow: unit -> DateTimeOffset
  abstract UtcNowRfc3339: unit -> string
  abstract UtcNowPlusDaysRfc3339: float -> string

type WebError<'appError> =
  | DbError of SqliteException
  | MissingHttpContext
  | AppError of 'appError

type CookieSpec =
  { Domain: string option
    Expires: DateTimeOffset option
    HttpOnly: bool
    IsEssential: bool
    MaxAge: TimeSpan option
    Path: string option
    SameSite: SameSiteMode option
    Secure: bool }

module CookieSpec =
  let empty: CookieSpec =
    { Domain = None
      Expires = None
      HttpOnly = false
      IsEssential = false
      MaxAge = None
      Path = None
      SameSite = None
      Secure = false }

  let toAspNetCore (spec: CookieSpec) =
    let cookieOptions = CookieOptions()
    cookieOptions.HttpOnly <- spec.HttpOnly
    cookieOptions.IsEssential <- spec.IsEssential
    cookieOptions.Secure <- spec.Secure

    match spec.Domain with
    | Some domain -> cookieOptions.Domain <- domain
    | None -> ()

    match spec.Expires with
    | Some expires -> cookieOptions.Expires <- Nullable expires
    | None -> ()

    match spec.MaxAge with
    | Some maxAge -> cookieOptions.MaxAge <- Nullable maxAge
    | None -> ()

    match spec.Path with
    | Some path -> cookieOptions.Path <- path
    | None -> ()

    match spec.SameSite with
    | Some sameSite -> cookieOptions.SameSite <- sameSite
    | None -> ()

    cookieOptions

type JsonPayload =
  { Value: obj option
    ValueType: Type
    Options: JsonSerializerOptions option }

type ResponseEffect<'custom> =
  | SetStatusCode of int
  | SetHeader of string * string
  | AppendHeader of string * string
  | WriteText of contentType: string * content: string
  | WriteBytes of contentType: string * content: byte array
  | WriteJson of JsonPayload
  | Redirect of location: string * permanent: bool
  | SetCookie of name: string * value: string * spec: CookieSpec
  | DeleteCookie of name: string * spec: CookieSpec option
  | Custom of 'custom

type WebCtx<'env, 'custom> =
  { env: 'env
    tx: SqliteTransaction
    httpContext: HttpContext option
    responsePlan: ResizeArray<ResponseEffect<'custom>> }

type WebOp<'env, 'appError, 'custom, 'a> = WebCtx<'env, 'custom> -> Task<Result<'a, WebError<'appError>>>

type WebRuntime<'env, 'appError, 'custom> =
  { Env: 'env
    JsonOptions: JsonSerializerOptions
    ApplyCustomEffect: HttpContext -> 'custom -> Task<Result<unit, WebError<'appError>>> }

module private WebOp =
  let zero () : WebOp<'env, 'appError, 'custom, unit> = fun _ -> Task.FromResult(Ok())

  let result (value: 'a) : WebOp<'env, 'appError, 'custom, 'a> = fun _ -> Task.FromResult(Ok value)

  let ofAppResult (result: Result<'a, 'appError>) : WebOp<'env, 'appError, 'custom, 'a> =
    fun _ -> Task.FromResult(result |> Result.mapError AppError)

  let ofAppTaskResult (resultTask: Task<Result<'a, 'appError>>) : WebOp<'env, 'appError, 'custom, 'a> =
    fun _ ->
      task {
        let! result = resultTask
        return result |> Result.mapError AppError
      }

  let ofTxnAppResult (step: TxnStep<Result<'a, 'appError>>) : WebOp<'env, 'appError, 'custom, 'a> =
    fun ctx ->
      task {
        let! result = step ctx.tx

        match result with
        | Error ex -> return Error(DbError ex)
        | Ok appResult -> return appResult |> Result.mapError AppError
      }

  let ofWebResult (result: Result<'a, WebError<'appError>>) : WebOp<'env, 'appError, 'custom, 'a> =
    fun _ -> Task.FromResult result

  let ofWebTaskResult (resultTask: Task<Result<'a, WebError<'appError>>>) : WebOp<'env, 'appError, 'custom, 'a> =
    fun _ -> resultTask

  let ofTxnStep (step: TxnStep<'a>) : WebOp<'env, 'appError, 'custom, 'a> =
    fun ctx ->
      task {
        let! result = step ctx.tx
        return result |> Result.mapError DbError
      }

  let bind
    (operation: WebOp<'env, 'appError, 'custom, 'a>)
    (continuation: 'a -> WebOp<'env, 'appError, 'custom, 'b>)
    : WebOp<'env, 'appError, 'custom, 'b> =
    fun ctx ->
      task {
        let! result = operation ctx

        match result with
        | Ok value -> return! continuation value ctx
        | Error error -> return Error error
      }

  let bindTask
    (valueTask: Task<'a>)
    (continuation: 'a -> WebOp<'env, 'appError, 'custom, 'b>)
    : WebOp<'env, 'appError, 'custom, 'b> =
    fun ctx ->
      task {
        let! value = valueTask
        return! continuation value ctx
      }

  let bindEffectTask
    (effectTask: Task)
    (continuation: unit -> WebOp<'env, 'appError, 'custom, 'a>)
    : WebOp<'env, 'appError, 'custom, 'a> =
    fun ctx ->
      task {
        do! effectTask
        return! continuation () ctx
      }

  let combine
    (first: WebOp<'env, 'appError, 'custom, unit>)
    (second: WebOp<'env, 'appError, 'custom, 'a>)
    : WebOp<'env, 'appError, 'custom, 'a> =
    bind first (fun () -> second)

  let delay (delayed: unit -> WebOp<'env, 'appError, 'custom, 'a>) : WebOp<'env, 'appError, 'custom, 'a> =
    fun ctx -> delayed () ctx

  let tryWith
    (delayed: unit -> WebOp<'env, 'appError, 'custom, 'a>)
    (handler: exn -> WebOp<'env, 'appError, 'custom, 'a>)
    : WebOp<'env, 'appError, 'custom, 'a> =
    fun ctx ->
      task {
        try
          return! delayed () ctx
        with ex ->
          return! handler ex ctx
      }

  let tryFinally
    (delayed: unit -> WebOp<'env, 'appError, 'custom, 'a>)
    (compensation: unit -> unit)
    : WebOp<'env, 'appError, 'custom, 'a> =
    fun ctx ->
      task {
        try
          return! delayed () ctx
        finally
          compensation ()
      }

  let using
    (resource: 'resource)
    (body: 'resource -> WebOp<'env, 'appError, 'custom, 'a>)
    : WebOp<'env, 'appError, 'custom, 'a> when 'resource :> IDisposable =
    tryFinally (fun () -> body resource) (fun () ->
      if not (isNull (box resource)) then
        resource.Dispose())

  let rec whileLoop
    (guard: unit -> bool)
    (body: WebOp<'env, 'appError, 'custom, unit>)
    : WebOp<'env, 'appError, 'custom, unit> =
    if not (guard ()) then
      zero ()
    else
      combine body (delay (fun () -> whileLoop guard body))

  let forEach
    (items: 'a seq)
    (body: 'a -> WebOp<'env, 'appError, 'custom, unit>)
    : WebOp<'env, 'appError, 'custom, unit> =
    fun ctx ->
      task {
        let mutable error = None
        use enumerator = items.GetEnumerator()

        while error.IsNone && enumerator.MoveNext() do
          let! result = body enumerator.Current ctx

          match result with
          | Ok() -> ()
          | Error webError -> error <- Some webError

        match error with
        | Some webError -> return Error webError
        | None -> return Ok()
      }

type WebResultBuilder() =
  member _.Return(value: 'a) : WebOp<'env, 'appError, 'custom, 'a> = WebOp.result value

  member _.ReturnFrom(operation: WebOp<'env, 'appError, 'custom, 'a>) : WebOp<'env, 'appError, 'custom, 'a> = operation

  member _.ReturnFrom(result: Result<'a, 'appError>) : WebOp<'env, 'appError, 'custom, 'a> = WebOp.ofAppResult result

  member _.ReturnFrom(result: Result<'a, WebError<'appError>>) : WebOp<'env, 'appError, 'custom, 'a> =
    WebOp.ofWebResult result

  member _.ReturnFrom(resultTask: Task<Result<'a, 'appError>>) : WebOp<'env, 'appError, 'custom, 'a> =
    WebOp.ofAppTaskResult resultTask

  member _.ReturnFrom(resultTask: Task<Result<'a, WebError<'appError>>>) : WebOp<'env, 'appError, 'custom, 'a> =
    WebOp.ofWebTaskResult resultTask

  member _.ReturnFrom(step: TxnStep<'a>) : WebOp<'env, 'appError, 'custom, 'a> = WebOp.ofTxnStep step

  member _.Bind
    (operation: WebOp<'env, 'appError, 'custom, 'a>, continuation: 'a -> WebOp<'env, 'appError, 'custom, 'b>)
    : WebOp<'env, 'appError, 'custom, 'b> =
    WebOp.bind operation continuation

  member _.Bind
    (result: Result<'a, 'appError>, continuation: 'a -> WebOp<'env, 'appError, 'custom, 'b>)
    : WebOp<'env, 'appError, 'custom, 'b> =
    WebOp.bind (WebOp.ofAppResult result) continuation

  member _.Bind
    (result: Result<'a, WebError<'appError>>, continuation: 'a -> WebOp<'env, 'appError, 'custom, 'b>)
    : WebOp<'env, 'appError, 'custom, 'b> =
    WebOp.bind (WebOp.ofWebResult result) continuation

  member _.Bind
    (resultTask: Task<Result<'a, 'appError>>, continuation: 'a -> WebOp<'env, 'appError, 'custom, 'b>)
    : WebOp<'env, 'appError, 'custom, 'b> =
    WebOp.bind (WebOp.ofAppTaskResult resultTask) continuation

  member _.Bind
    (resultTask: Task<Result<'a, WebError<'appError>>>, continuation: 'a -> WebOp<'env, 'appError, 'custom, 'b>)
    : WebOp<'env, 'appError, 'custom, 'b> =
    WebOp.bind (WebOp.ofWebTaskResult resultTask) continuation

  member _.Bind
    (valueTask: Task<'a>, continuation: 'a -> WebOp<'env, 'appError, 'custom, 'b>)
    : WebOp<'env, 'appError, 'custom, 'b> =
    WebOp.bindTask valueTask continuation

  member _.Bind
    (effectTask: Task, continuation: unit -> WebOp<'env, 'appError, 'custom, 'a>)
    : WebOp<'env, 'appError, 'custom, 'a> =
    WebOp.bindEffectTask effectTask continuation

  member _.Bind
    (step: TxnStep<'a>, continuation: 'a -> WebOp<'env, 'appError, 'custom, 'b>)
    : WebOp<'env, 'appError, 'custom, 'b> =
    WebOp.bind (WebOp.ofTxnStep step) continuation

  member _.Zero() : WebOp<'env, 'appError, 'custom, unit> = WebOp.zero ()

  member _.Delay(delayed: unit -> WebOp<'env, 'appError, 'custom, 'a>) : unit -> WebOp<'env, 'appError, 'custom, 'a> =
    delayed

  member _.Combine
    (first: WebOp<'env, 'appError, 'custom, unit>, second: WebOp<'env, 'appError, 'custom, 'a>)
    : WebOp<'env, 'appError, 'custom, 'a> =
    WebOp.combine first second

  member _.TryWith
    (delayed: unit -> WebOp<'env, 'appError, 'custom, 'a>, handler: exn -> WebOp<'env, 'appError, 'custom, 'a>)
    : WebOp<'env, 'appError, 'custom, 'a> =
    WebOp.tryWith delayed handler

  member _.TryFinally
    (delayed: unit -> WebOp<'env, 'appError, 'custom, 'a>, compensation: unit -> unit)
    : WebOp<'env, 'appError, 'custom, 'a> =
    WebOp.tryFinally delayed compensation

  member _.Using
    (resource: 'resource, body: 'resource -> WebOp<'env, 'appError, 'custom, 'a>)
    : WebOp<'env, 'appError, 'custom, 'a> when 'resource :> IDisposable =
    WebOp.using resource body

  member _.While
    (guard: unit -> bool, body: WebOp<'env, 'appError, 'custom, unit>)
    : WebOp<'env, 'appError, 'custom, unit> =
    WebOp.whileLoop guard body

  member _.For
    (items: 'a seq, body: 'a -> WebOp<'env, 'appError, 'custom, unit>)
    : WebOp<'env, 'appError, 'custom, unit> =
    WebOp.forEach items body

  member _.Run(delayed: unit -> WebOp<'env, 'appError, 'custom, 'a>) : WebOp<'env, 'appError, 'custom, 'a> = delayed ()

let webResult = WebResultBuilder()

module Web =
  let env: WebOp<'env, 'appError, 'custom, 'env> =
    fun ctx -> Task.FromResult(Ok ctx.env)

  let tx: WebOp<'env, 'appError, 'custom, SqliteTransaction> =
    fun ctx -> Task.FromResult(Ok ctx.tx)

  let tryHttpContext: WebOp<'env, 'appError, 'custom, HttpContext option> =
    fun ctx -> Task.FromResult(Ok ctx.httpContext)

  let httpContext: WebOp<'env, 'appError, 'custom, HttpContext> =
    fun ctx ->
      Task.FromResult(
        match ctx.httpContext with
        | Some httpContext -> Ok httpContext
        | None -> Error MissingHttpContext
      )

  let fail (error: 'appError) : WebOp<'env, 'appError, 'custom, 'a> =
    fun _ -> Task.FromResult(Error(AppError error))

  let failWeb (error: WebError<'appError>) : WebOp<'env, 'appError, 'custom, 'a> = fun _ -> Task.FromResult(Error error)

  let ignore (operation: WebOp<'env, 'appError, 'custom, 'a>) : WebOp<'env, 'appError, 'custom, unit> =
    WebOp.bind operation (fun _ -> WebOp.result ())

  let requireSome (error: 'appError) (value: 'a option) : WebOp<'env, 'appError, 'custom, 'a> =
    match value with
    | Some resolved -> fun _ -> Task.FromResult(Ok resolved)
    | None -> fail error

  let ofAppResult (result: Result<'a, 'appError>) : WebOp<'env, 'appError, 'custom, 'a> = WebOp.ofAppResult result

  let ofAppTaskResult (resultTask: Task<Result<'a, 'appError>>) : WebOp<'env, 'appError, 'custom, 'a> =
    WebOp.ofAppTaskResult resultTask

  let ofTxnAppResult (step: TxnStep<Result<'a, 'appError>>) : WebOp<'env, 'appError, 'custom, 'a> =
    WebOp.ofTxnAppResult step

  let ofWebResult (result: Result<'a, WebError<'appError>>) : WebOp<'env, 'appError, 'custom, 'a> =
    WebOp.ofWebResult result

module Clock =
  let utcNow: WebOp<'env, 'appError, 'custom, DateTimeOffset> when 'env :> IClock =
    fun ctx ->
      let clock = ctx.env :> IClock
      Task.FromResult(Ok(clock.UtcNow()))

  let utcNowRfc3339: WebOp<'env, 'appError, 'custom, string> when 'env :> IClock =
    fun ctx ->
      let clock = ctx.env :> IClock
      Task.FromResult(Ok(clock.UtcNowRfc3339()))

  let utcNowPlusDaysRfc3339 (days: float) : WebOp<'env, 'appError, 'custom, string> when 'env :> IClock =
    fun ctx ->
      let clock = ctx.env :> IClock
      Task.FromResult(Ok(clock.UtcNowPlusDaysRfc3339 days))

module private Response =
  let append (effect: ResponseEffect<'custom>) : WebOp<'env, 'appError, 'custom, unit> =
    fun ctx ->
      Task.FromResult(
        match ctx.httpContext with
        | Some _ ->
          ctx.responsePlan.Add effect
          Ok()
        | None -> Error MissingHttpContext
      )

module Respond =
  let statusCode (statusCode: int) : WebOp<'env, 'appError, 'custom, unit> =
    Response.append (SetStatusCode statusCode)

  let header (name: string) (value: string) : WebOp<'env, 'appError, 'custom, unit> =
    Response.append (SetHeader(name, value))

  let appendHeader (name: string) (value: string) : WebOp<'env, 'appError, 'custom, unit> =
    Response.append (AppendHeader(name, value))

  let text (content: string) : WebOp<'env, 'appError, 'custom, unit> =
    Response.append (WriteText("text/plain; charset=utf-8", content))

  let html (content: string) : WebOp<'env, 'appError, 'custom, unit> =
    Response.append (WriteText("text/html; charset=utf-8", content))

  let bytes (contentType: string) (content: byte array) : WebOp<'env, 'appError, 'custom, unit> =
    Response.append (WriteBytes(contentType, content))

  let json<'env, 'appError, 'custom, 'a> (value: 'a) : WebOp<'env, 'appError, 'custom, unit> =
    let boxedValue =
      match box value with
      | null -> None
      | boxed -> Some boxed

    Response.append (
      (WriteJson
        { Value = boxedValue
          ValueType = typeof<'a>
          Options = None }
      : ResponseEffect<'custom>)
    )

  let jsonWith<'env, 'appError, 'custom, 'a>
    (options: JsonSerializerOptions)
    (value: 'a)
    : WebOp<'env, 'appError, 'custom, unit> =
    let boxedValue =
      match box value with
      | null -> None
      | boxed -> Some boxed

    Response.append (
      (WriteJson
        { Value = boxedValue
          ValueType = typeof<'a>
          Options = Some options }
      : ResponseEffect<'custom>)
    )

  let redirect (location: string) : WebOp<'env, 'appError, 'custom, unit> =
    Response.append (Redirect(location, false))

  let permanentRedirect (location: string) : WebOp<'env, 'appError, 'custom, unit> =
    Response.append (Redirect(location, true))

  let setCookie (name: string) (value: string) (spec: CookieSpec) : WebOp<'env, 'appError, 'custom, unit> =
    Response.append (SetCookie(name, value, spec))

  let deleteCookie (name: string) (spec: CookieSpec option) : WebOp<'env, 'appError, 'custom, unit> =
    Response.append (DeleteCookie(name, spec))

  let custom (effect: 'custom) : WebOp<'env, 'appError, 'custom, unit> = Response.append (Custom effect)

module WebRuntime =
  let create
    (env: 'env)
    (applyCustomEffect: HttpContext -> 'custom -> Task<Result<unit, WebError<'appError>>>)
    : WebRuntime<'env, 'appError, 'custom> =
    { Env = env
      JsonOptions = JsonSerializerOptions JsonSerializerDefaults.Web
      ApplyCustomEffect = applyCustomEffect }

  let withJsonOptions
    (jsonOptions: JsonSerializerOptions)
    (runtime: WebRuntime<'env, 'appError, 'custom>)
    : WebRuntime<'env, 'appError, 'custom> =
    { runtime with
        JsonOptions = jsonOptions }

  let createSimple (env: 'env) : WebRuntime<'env, 'appError, unit> =
    create env (fun _ () -> Task.FromResult(Ok()))

let private applyEffect
  (runtime: WebRuntime<'env, 'appError, 'custom>)
  (httpContext: HttpContext)
  (effect: ResponseEffect<'custom>)
  : Task<Result<unit, WebError<'appError>>> =
  task {
    match effect with
    | SetStatusCode statusCode ->
      httpContext.Response.StatusCode <- statusCode
      return Ok()
    | SetHeader(name, value) ->
      httpContext.Response.Headers[name] <- value
      return Ok()
    | AppendHeader(name, value) ->
      httpContext.Response.Headers.Append(name, value)
      return Ok()
    | WriteText(contentType, content) ->
      httpContext.Response.ContentType <- contentType
      do! httpContext.Response.WriteAsync content
      return Ok()
    | WriteBytes(contentType, content) ->
      httpContext.Response.ContentType <- contentType
      do! httpContext.Response.Body.WriteAsync(content, 0, content.Length)
      return Ok()
    | WriteJson payload ->
      let options = payload.Options |> Option.defaultValue runtime.JsonOptions

      let json =
        match payload.Value with
        | Some value -> JsonSerializer.Serialize(value, payload.ValueType, options)
        | None -> "null"

      httpContext.Response.ContentType <- "application/json; charset=utf-8"
      do! httpContext.Response.WriteAsync json
      return Ok()
    | Redirect(location, permanent) ->
      httpContext.Response.Redirect(location, permanent)
      return Ok()
    | SetCookie(name, value, spec) ->
      httpContext.Response.Cookies.Append(name, value, CookieSpec.toAspNetCore spec)
      return Ok()
    | DeleteCookie(name, spec) ->
      match spec with
      | Some cookieSpec -> httpContext.Response.Cookies.Delete(name, CookieSpec.toAspNetCore cookieSpec)
      | None -> httpContext.Response.Cookies.Delete name

      return Ok()
    | Custom custom -> return! runtime.ApplyCustomEffect httpContext custom
  }

let private applyResponsePlan
  (runtime: WebRuntime<'env, 'appError, 'custom>)
  (httpContext: HttpContext option)
  (responsePlan: ResizeArray<ResponseEffect<'custom>>)
  : Task<Result<unit, WebError<'appError>>> =
  task {
    match httpContext with
    | None when responsePlan.Count = 0 -> return Ok()
    | None -> return Error MissingHttpContext
    | Some httpContext ->
      let mutable error = None
      let mutable index = 0

      while error.IsNone && index < responsePlan.Count do
        let effect = responsePlan[index]
        index <- index + 1
        let! result = applyEffect runtime httpContext effect

        match result with
        | Ok() -> ()
        | Error webError -> error <- Some webError

      match error with
      | Some webError -> return Error webError
      | None -> return Ok()
  }

let run
  (runtime: WebRuntime<'env, 'appError, 'custom>)
  (httpContext: HttpContext option)
  (operation: WebOp<'env, 'appError, 'custom, 'a>)
  : Task<Result<'a, WebError<'appError>>> when 'env :> IHasDbRuntime =
  task {
    let responsePlan = ResizeArray<ResponseEffect<'custom>>()

    let! result =
      runtime.Env.DbRuntime.RunInTransaction DbError (fun tx ->
        operation
          { env = runtime.Env
            tx = tx
            httpContext = httpContext
            responsePlan = responsePlan })

    match result with
    | Error error -> return Error error
    | Ok value ->
      let! responseResult = applyResponsePlan runtime httpContext responsePlan

      match responseResult with
      | Ok() -> return Ok value
      | Error error -> return Error error
  }

let runSimple
  (env: 'env)
  (httpContext: HttpContext option)
  (operation: WebOp<'env, 'appError, unit, 'a>)
  : Task<Result<'a, WebError<'appError>>> when 'env :> IHasDbRuntime =
  run (WebRuntime.createSimple env) httpContext operation
