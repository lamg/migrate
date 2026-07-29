namespace MigLib

module TaskResult =
  open System
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

    member this.Using(resource: 'a :> IDisposable, binder: 'a -> Result<'b, 'e>) : Result<'b, 'e> =
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

  module private TaskResultImpl =
    let result (x: 'a) : Task<Result<'a, 'e>> = Task.FromResult(Ok x)
    let returnFrom (m: Task<Result<'a, 'e>>) : Task<Result<'a, 'e>> = m
    let returnFromResult (m: Result<'a, 'e>) : Task<Result<'a, 'e>> = Task.FromResult m

    let returnFromTask (m: Task<'a>) : Task<Result<'a, 'e>> =
      task {
        let! value = m
        return Ok value
      }

    let bind (m: Task<Result<'a, 'e>>) (f: 'a -> Task<Result<'b, 'e>>) : Task<Result<'b, 'e>> =
      task {
        let! result = m

        match result with
        | Ok value -> return! f value
        | Error ex -> return Error ex
      }

    let bindResult (m: Result<'a, 'e>) (f: 'a -> Task<Result<'b, 'e>>) : Task<Result<'b, 'e>> =
      match m with
      | Ok value -> f value
      | Error ex -> Task.FromResult(Error ex)

    let bindTask (m: Task<'a>) (f: 'a -> Task<Result<'b, 'e>>) : Task<Result<'b, 'e>> =
      task {
        let! value = m
        return! f value
      }

    let combine (m: Task<Result<unit, 'e>>) (f: unit -> Task<Result<'a, 'e>>) : Task<Result<'a, 'e>> =
      bind m (fun () -> f ())

    let delay (f: unit -> Task<Result<'a, 'e>>) = f
    let run (f: unit -> Task<Result<'a, 'e>>) = f ()

    let tryWith (body: unit -> Task<Result<'a, 'e>>) (handler: exn -> Task<Result<'a, 'e>>) =
      task {
        try
          return! body ()
        with ex ->
          return! handler ex
      }

    let tryFinally (body: unit -> Task<Result<'a, 'e>>) (compensation: unit -> unit) =
      task {
        try
          return! body ()
        finally
          compensation ()
      }

    let using (resource: 'a :> IDisposable) (body: 'a -> Task<Result<'b, 'e>>) =
      tryFinally (fun () -> body resource) (fun () ->
        if not (isNull (box resource)) then
          resource.Dispose())

    let rec whileLoop (guard: unit -> bool) (body: unit -> Task<Result<unit, 'e>>) =
      if not (guard ()) then
        result ()
      else
        bind (body ()) (fun () -> whileLoop guard body)

    let forEach (items: seq<'a>) (body: 'a -> Task<Result<unit, 'e>>) =
      use enumerator = items.GetEnumerator()
      whileLoop enumerator.MoveNext (fun () -> body enumerator.Current)

  type TaskResultBuilder() =
    member _.Return(x: 'a) = TaskResultImpl.result x
    member _.ReturnFrom(m: Task<Result<'a, 'e>>) = TaskResultImpl.returnFrom m
    member _.ReturnFrom(m: Result<'a, 'e>) = TaskResultImpl.returnFromResult m
    member _.ReturnFrom(m: Task<'a>) = TaskResultImpl.returnFromTask m
    member _.Bind(m: Task<Result<'a, 'e>>, f: 'a -> Task<Result<'b, 'e>>) = TaskResultImpl.bind m f
    member _.Bind(m: Result<'a, 'e>, f: 'a -> Task<Result<'b, 'e>>) = TaskResultImpl.bindResult m f
    member _.Bind(m: Task<'a>, f: 'a -> Task<Result<'b, 'e>>) = TaskResultImpl.bindTask m f
    member _.Zero() = TaskResultImpl.result ()
    member _.Combine(m: Task<Result<unit, 'e>>, f: unit -> Task<Result<'a, 'e>>) = TaskResultImpl.combine m f
    member _.Delay(f: unit -> Task<Result<'a, 'e>>) = TaskResultImpl.delay f
    member _.Run(f: unit -> Task<Result<'a, 'e>>) = TaskResultImpl.run f
    member _.TryWith(body, handler) = TaskResultImpl.tryWith body handler
    member _.TryFinally(body, compensation) = TaskResultImpl.tryFinally body compensation
    member _.Using(resource: 'a :> IDisposable, body) = TaskResultImpl.using resource body
    member _.While(guard, body) = TaskResultImpl.whileLoop guard body
    member _.For(items, body) = TaskResultImpl.forEach items body

  let taskResult = TaskResultBuilder()

