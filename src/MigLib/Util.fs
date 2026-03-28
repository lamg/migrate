module MigLib.Util

open System
open Microsoft.Data.Sqlite
open System.Threading.Tasks

type ResultBuilder() =
  member _.Return(value: 'a) : Result<'a, 'e> = Ok value

  member _.ReturnFrom(result: Result<'a, 'e>) : Result<'a, 'e> = result

  member _.Bind(result: Result<'a, 'e>, binder: 'a -> Result<'b, 'e>) : Result<'b, 'e> = Result.bind binder result

  member _.Zero() : Result<unit, 'e> = Ok()

  member _.Delay(generator: unit -> Result<'a, 'e>) : unit -> Result<'a, 'e> = generator

  member _.Run(generator: unit -> Result<'a, 'e>) : Result<'a, 'e> = generator ()

  member _.Combine(result: Result<unit, 'e>, generator: unit -> Result<'a, 'e>) : Result<'a, 'e> =
    match result with
    | Ok() -> generator ()
    | Error error -> Error error

  member _.TryWith(generator: unit -> Result<'a, 'e>, handler: exn -> Result<'a, 'e>) : Result<'a, 'e> =
    try
      generator ()
    with ex ->
      handler ex

  member _.TryFinally(generator: unit -> Result<'a, 'e>, compensation: unit -> unit) : Result<'a, 'e> =
    try
      generator ()
    finally
      compensation ()

  member this.Using(resource: 'a :> System.IDisposable, binder: 'a -> Result<'b, 'e>) : Result<'b, 'e> =
    this.TryFinally(
      (fun () -> binder resource),
      fun () ->
        if not (isNull (box resource)) then
          resource.Dispose()
    )

  member this.While(guard: unit -> bool, body: unit -> Result<unit, 'e>) : Result<unit, 'e> =
    if not (guard ()) then
      this.Zero()
    else
      this.Bind(body (), fun () -> this.While(guard, body))

  member this.For(sequence: 'a seq, binder: 'a -> Result<unit, 'e>) : Result<unit, 'e> =
    use enumerator = sequence.GetEnumerator()

    this.While(enumerator.MoveNext, fun () -> binder enumerator.Current)

let result = ResultBuilder()

module ResultEx =
  let requireSome (error: 'e) (value: 'a option) : Result<'a, 'e> =
    match value with
    | Some item -> Ok item
    | None -> Error error

  let requireSomeWith (errorFactory: unit -> 'e) (value: 'a option) : Result<'a, 'e> =
    match value with
    | Some item -> Ok item
    | None -> Error(errorFactory ())

  let orFail (toException: 'e -> exn) (value: Result<'a, 'e>) : 'a =
    match value with
    | Ok item -> item
    | Error error -> raise (toException error)

  let zip2 (left: Result<'a, 'e>) (right: Result<'b, 'e>) : Result<'a * 'b, 'e> =
    result {
      let! leftValue = left
      let! rightValue = right
      return leftValue, rightValue
    }

  let zip3 (first: Result<'a, 'e>) (second: Result<'b, 'e>) (third: Result<'c, 'e>) : Result<'a * 'b * 'c, 'e> =
    result {
      let! firstValue = first
      let! secondValue = second
      let! thirdValue = third
      return firstValue, secondValue, thirdValue
    }

  let zip4
    (first: Result<'a, 'e>)
    (second: Result<'b, 'e>)
    (third: Result<'c, 'e>)
    (fourth: Result<'d, 'e>)
    : Result<'a * 'b * 'c * 'd, 'e> =
    result {
      let! firstValue = first
      let! secondValue = second
      let! thirdValue = third
      let! fourthValue = fourth
      return firstValue, secondValue, thirdValue, fourthValue
    }

module TaskResultEx =
  let ofResultMapError (mapError: 'error -> 'mappedError) (value: Result<'a, 'error>) : Task<Result<'a, 'mappedError>> =
    value |> Result.mapError mapError |> Task.FromResult

module private TaskResult =
  let result (x: 'a) : Task<Result<'a, SqliteException>> = Task.FromResult(Ok x)

  let returnFrom (m: Task<Result<'a, SqliteException>>) : Task<Result<'a, SqliteException>> = m

  let returnFromResult (m: Result<'a, SqliteException>) : Task<Result<'a, SqliteException>> = Task.FromResult m

  let returnFromTask (m: Task<'a>) : Task<Result<'a, SqliteException>> =
    task {
      let! value = m
      return Ok value
    }

  let bind
    (m: Task<Result<'a, SqliteException>>)
    (f: 'a -> Task<Result<'b, SqliteException>>)
    : Task<Result<'b, SqliteException>> =
    task {
      let! result = m

      match result with
      | Ok value -> return! f value
      | Error ex -> return Error ex
    }

  let bindResult
    (m: Result<'a, SqliteException>)
    (f: 'a -> Task<Result<'b, SqliteException>>)
    : Task<Result<'b, SqliteException>> =
    match m with
    | Ok value -> f value
    | Error ex -> Task.FromResult(Error ex)

  let bindTask (m: Task<'a>) (f: 'a -> Task<Result<'b, SqliteException>>) : Task<Result<'b, SqliteException>> =
    task {
      let! value = m
      return! f value
    }

  let combine
    (m: Task<Result<unit, SqliteException>>)
    (f: unit -> Task<Result<'a, SqliteException>>)
    : Task<Result<'a, SqliteException>> =
    bind m (fun () -> f ())

  let delay (f: unit -> Task<Result<'a, SqliteException>>) : unit -> Task<Result<'a, SqliteException>> = f

  let run (f: unit -> Task<Result<'a, SqliteException>>) : Task<Result<'a, SqliteException>> = f ()

  let tryWith
    (body: unit -> Task<Result<'a, SqliteException>>)
    (handler: exn -> Task<Result<'a, SqliteException>>)
    : Task<Result<'a, SqliteException>> =
    task {
      try
        return! body ()
      with ex ->
        return! handler ex
    }

  let tryFinally
    (body: unit -> Task<Result<'a, SqliteException>>)
    (compensation: unit -> unit)
    : Task<Result<'a, SqliteException>> =
    task {
      try
        return! body ()
      finally
        compensation ()
    }

  let using
    (resource: 'a :> IDisposable)
    (body: 'a -> Task<Result<'b, SqliteException>>)
    : Task<Result<'b, SqliteException>> =
    tryFinally (fun () -> body resource) (fun () ->
      if not (isNull (box resource)) then
        resource.Dispose())

  let rec whileLoop
    (guard: unit -> bool)
    (body: unit -> Task<Result<unit, SqliteException>>)
    : Task<Result<unit, SqliteException>> =
    if not (guard ()) then
      result ()
    else
      bind (body ()) (fun () -> whileLoop guard body)

  let forEach (items: seq<'a>) (body: 'a -> Task<Result<unit, SqliteException>>) : Task<Result<unit, SqliteException>> =
    use enumerator = items.GetEnumerator()
    whileLoop enumerator.MoveNext (fun () -> body enumerator.Current)

type TaskResultBuilder() =
  member _.Return(x: 'a) : Task<Result<'a, SqliteException>> = TaskResult.result x

  member _.ReturnFrom(m: Task<Result<'a, SqliteException>>) : Task<Result<'a, SqliteException>> =
    TaskResult.returnFrom m

  member _.ReturnFrom(m: Result<'a, SqliteException>) : Task<Result<'a, SqliteException>> =
    TaskResult.returnFromResult m

  member _.ReturnFrom(m: Task<'a>) : Task<Result<'a, SqliteException>> = TaskResult.returnFromTask m

  member _.Bind
    (m: Task<Result<'a, SqliteException>>, f: 'a -> Task<Result<'b, SqliteException>>)
    : Task<Result<'b, SqliteException>> =
    TaskResult.bind m f

  member _.Bind
    (m: Result<'a, SqliteException>, f: 'a -> Task<Result<'b, SqliteException>>)
    : Task<Result<'b, SqliteException>> =
    TaskResult.bindResult m f

  member _.Bind(m: Task<'a>, f: 'a -> Task<Result<'b, SqliteException>>) : Task<Result<'b, SqliteException>> =
    TaskResult.bindTask m f

  member _.Zero() : Task<Result<unit, SqliteException>> = TaskResult.result ()

  member _.Combine
    (m: Task<Result<unit, SqliteException>>, f: unit -> Task<Result<'a, SqliteException>>)
    : Task<Result<'a, SqliteException>> =
    TaskResult.combine m f

  member _.Delay(f: unit -> Task<Result<'a, SqliteException>>) : unit -> Task<Result<'a, SqliteException>> =
    TaskResult.delay f

  member _.Run(f: unit -> Task<Result<'a, SqliteException>>) : Task<Result<'a, SqliteException>> = TaskResult.run f

  member _.TryWith
    (body: unit -> Task<Result<'a, SqliteException>>, handler: exn -> Task<Result<'a, SqliteException>>)
    : Task<Result<'a, SqliteException>> =
    TaskResult.tryWith body handler

  member _.TryFinally
    (body: unit -> Task<Result<'a, SqliteException>>, compensation: unit -> unit)
    : Task<Result<'a, SqliteException>> =
    TaskResult.tryFinally body compensation

  member _.Using
    (resource: 'a :> IDisposable, body: 'a -> Task<Result<'b, SqliteException>>)
    : Task<Result<'b, SqliteException>> =
    TaskResult.using resource body

  member _.While
    (guard: unit -> bool, body: unit -> Task<Result<unit, SqliteException>>)
    : Task<Result<unit, SqliteException>> =
    TaskResult.whileLoop guard body

  member _.For(items: seq<'a>, body: 'a -> Task<Result<unit, SqliteException>>) : Task<Result<unit, SqliteException>> =
    TaskResult.forEach items body

let taskResult = TaskResultBuilder()

let traverseResultM (mapper: 'a -> Result<'b, 'e>) (items: 'a list) : Result<'b list, 'e> =
  let folder state item =
    match state, mapper item with
    | Ok collected, Ok mapped -> Ok(collected @ [ mapped ])
    | Error error, _ -> Error error
    | _, Error error -> Error error

  List.fold folder (Ok []) items
