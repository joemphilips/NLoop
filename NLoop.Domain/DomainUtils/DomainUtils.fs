namespace NLoop.Domain.Utils

open System.Text.Unicode
open DotNetLightning.Utils
open System
open NLoop.Domain
open System.Threading.Tasks
open EventStore.ClientAPI
open FsToolkit.ErrorHandling

/// Friendly name of the domain event type. Might be used later to query the event stream.
type EventType = EventType of string
  with
  member this.Value = let (EventType v) = this in v
  member this.ToBytes() =
    this.Value |> System.Text.Encoding.UTF8.GetBytes

  static member FromBytes(b: byte[]) =
    System.Text.Encoding.UTF8.GetString b
    |> EventType

type StreamId = StreamId of string
  with
  member internal this.Value = let (StreamId v) = this in v
  static member Create<'TEntityId> (entityType: string) (entityId: 'TEntityId) =
    entityType + "-" + entityId.ToString().ToLower()
    |> StreamId

type EventId = EventId of Guid

type EventNumber = private EventNumber of uint64
  with
  member this.Value = let (EventNumber v) = this in v
  static member Create(i: uint64) =
    EventNumber (i)

  static member Create(i: int64) =
    if (i < 0L) then Error ($"Negative event number %i{i}") else
    EventNumber(uint64 i)
    |> Ok

/// `AsOf` means as it was or will be on and after that date.
/// `AsAt` means as it is at that particular time only. It implies there may be changes.
/// `Latest` means as it currently is. Specifically, include all events in the stream.
type ObservationDate =
  | Latest
  | AsOf of DateTime
  | AsAt of DateTime

type StoreError = StoreError of string
type EventSourcingError<'T> =
  | Store of StoreError
  | DomainError of 'T

type EventMeta = {
  /// Date at which event is effective in the domain
  EffectiveDate: DateTime
  /// Origin of this event.
  SourceName: string
}
  with
  member this.ToBytes() =
    let d =
      this.EffectiveDate
      |> fun x -> DateTimeOffset(x, TimeSpan.Zero)
      |> NBitcoin.Utils.DateTimeToUnixTime
      |> fun u ->  NBitcoin.Utils.ToBytes(u, false).BytesWithLength()
    let source =
      this.SourceName
      |> System.Text.Encoding.UTF8.GetBytes
      |> fun b -> b.BytesWithLength()
    Array.concat [d; source]

  static member FromBytes(b: byte[]) =
    try
      let effectiveDate, b = b.PopWithLen()
      let sourceName, _b = b.PopWithLen()
      {
        EffectiveDate =
          effectiveDate
          |> fun b -> NBitcoin.Utils.ToUInt32(b, false)
          |>  NBitcoin.Utils.UnixTimeToDateTime
          |> fun dateTimeOffset -> dateTimeOffset.UtcDateTime
        SourceName =
          sourceName
          |> System.Text.Encoding.UTF8.GetString
      }
      |> Ok
    with
    | ex ->
      Error (sprintf "Failed to Deserialize EventMeta %A" ex)

type Serializer<'TEvent> = {
  EventToBytes: 'TEvent -> byte[]
  BytesToEvents: byte[] -> Result<'TEvent, string>
}

type Event<'TEvent> = {
  Type: EventType
  Data: 'TEvent
  Meta: EventMeta
}
  with
  member this.ToSerializedEvent (serializer: Serializer<'TEvent>) =
    {
      SerializedEvent.Data = this.Data |> serializer.EventToBytes
      Type = this.Type
      Meta = this.Meta.ToBytes()
    }

and SerializedEvent = {
  Type: EventType
  Data: byte[]
  Meta: byte[]
}
  with
  member this.ToBytes() =
    Array.concat (seq [
      this.Type.ToBytes().BytesWithLength()
      this.Data.BytesWithLength()
      this.Meta.BytesWithLength()
    ])

  static member FromBytes(b: byte[]) =
    let ty, b = b.PopWithLen()
    let data, b = b.PopWithLen()
    let meta, _b = b.PopWithLen()
    {
      Type = EventType.FromBytes(ty)
      Data = data
      Meta = meta
    }


type RecordedEvent<'TEvent> = {
  Id: EventId
  Type: EventType
  EventNumber: EventNumber
  CreatedDate: DateTime
  Data: 'TEvent
  Meta: EventMeta
}
  with
  member this.AsEvent =
    {
      Event.Data = this.Data
      Type = this.Type
      Meta = this.Meta
    }


type SerializedRecordedEvent = {
  Id: EventId
  Type: EventType
  EventNumber: EventNumber
  CreatedDate: DateTime
  Data: byte[]
  Meta: byte[]
}
  with
  member this.ToRecordedEvent<'TEvent>(serializer: Serializer<'TEvent>) = result {
    let! e = this.Data |> serializer.BytesToEvents
    let! m = this.Meta |> EventMeta.FromBytes
    return
      {
        RecordedEvent.Id = this.Id
        Type = this.Type
        EventNumber = this.EventNumber
        CreatedDate = this.CreatedDate
        Data = e
        Meta = m
      }
  }


type CommandMeta =
    { EffectiveDate: DateTime
      Source: string }

type ESCommand<'DomainCommand> =
    { Data: 'DomainCommand
      Meta: CommandMeta }

type ExecResult<'TEvent> =  {
  EventFinishedImmediately: Event<'TEvent> list
  EventFinishesInFuture: Cmd<Event<'TEvent>>
}
type Aggregate<'TState, 'TCommand, 'TEvent, 'TError,'T when 'T : comparison> = {
  Zero: 'TState
  Filter: RecordedEvent<'TEvent> list -> RecordedEvent<'TEvent> list
  Enrich: Event<'TEvent> list -> Event<'TEvent> list
  SortBy: Event<'TEvent> -> 'T
  Apply: 'TState -> 'TEvent -> 'TState
  Exec: 'TState -> ESCommand<'TCommand> -> Task<Result<Event<'TEvent> list * Cmd<Event<'TEvent>>, 'TError>>
}

type ExpectedVersionUnion =
  | Any
  | NoStream
  | StreamExists
  | Specific of int64
  with
  static member FromLastEvent(maybeLastEvent: SerializedRecordedEvent option) =
    match maybeLastEvent with
    | Some lastEvent ->
      lastEvent.EventNumber.Value
      |> int64
      |> Specific
    | None ->
      NoStream

type Store = {
  ReadLast: StreamId -> Task<Result<SerializedRecordedEvent option, StoreError>>
  ReadStream: StreamId -> Task<Result<SerializedRecordedEvent seq, StoreError>>
  WriteStream: ExpectedVersionUnion -> SerializedEvent list -> StreamId -> Task<Result<unit, StoreError>>
}

type Repository<'TEvent, 'TEntityId> = {
  Version: 'TEntityId -> Task<Result<ExpectedVersionUnion, StoreError>>
  Load: 'TEntityId -> Task<Result<RecordedEvent<'TEvent> list, StoreError>>
  Commit: 'TEntityId -> ExpectedVersionUnion -> Event<'TEvent> list -> Task<Result<unit, StoreError>>
}
  with
  static member Create(store: Store) (serializer: Serializer<'TEvent>)(entityType: string): Repository<'TEvent,'TEntityId> =
    let version (entityId: 'TEntityId) = taskResult {
      let! maybeLastEvent =
        entityId
        |> StreamId.Create entityType
        |> store.ReadLast
      return
        maybeLastEvent |> ExpectedVersionUnion.FromLastEvent
    }
    let load entityId = taskResult {
      let! serializedRecordedEvents =
        entityId
        |> StreamId.Create entityType
        |> store.ReadStream
      return!
        serializedRecordedEvents
        |> Seq.map(fun e -> e.ToRecordedEvent serializer)
        |> Seq.toList
        |> List.sequenceResultM
        |> Result.mapError(StoreError)
    }
    let commit (entityId: 'TEntityId) expectedVersion (events: Event<'TEvent> list) =
      let streamId =
        entityId
        |> StreamId.Create entityType
      let serializedEvents =
        events
        |> List.map(fun e -> e.ToSerializedEvent serializer)
      store.WriteStream expectedVersion serializedEvents streamId

    {
      Version = version
      Load = load
      Commit = commit
    }

type Handler<'TState, 'TCommand, 'TEvent, 'TError, 'TEntityId> = {
  Replay: 'TEntityId -> ObservationDate -> Task<Result<Event<'TEvent> list, EventSourcingError<'TError>>>
  Reconstitute: Event<'TEvent> list -> 'TState
  Execute: 'TEntityId -> ESCommand<'TCommand> -> Task<Result<Event<'TEvent> list, EventSourcingError<'TError>>>
}
  with
  static member Create
    (aggregate: Aggregate<'TState, 'TCommand, 'TEvent, 'TError, 'T>)
    (repo: Repository<'TEvent, 'TEntityId>) =
    let replay (entityId: 'TEntityId) (observationDate: ObservationDate) = taskResult {
      let! recordedEvents =
        repo.Load entityId
        |> TaskResult.mapError(EventSourcingError.Store)

      let onOrBeforeObservationDate
        ({ RecordedEvent.CreatedDate = cDate; Meta = { EffectiveDate  = eDate } }) =
        match observationDate with
        | Latest -> true
        | AsOf d -> cDate <= d
        | AsAt d -> eDate <= d
      return
        recordedEvents
        |> aggregate.Filter
        |> List.filter(onOrBeforeObservationDate)
        |> List.map(fun rEvent -> rEvent.AsEvent)
        |> aggregate.Enrich
        |> List.sortBy aggregate.SortBy
    }

    let reconstitute
      (events: Event<'TEvent> list) =
      let folder (acc) (event: Event<'TEvent>) =
        // we do not have to perform side effects when reconstituting the state
        let nextState = aggregate.Apply acc event.Data
        nextState
      events
      |> List.fold(folder) (aggregate.Zero)

    let rec execute
      (entityId: 'TEntityId)
      (command: ESCommand<'TCommand>) = taskResult {
        let { ESCommand.Meta = { EffectiveDate = date } } = command
        let! expectedVersion =
          repo.Version entityId
          |> TaskResult.mapError EventSourcingError.Store
        let! recordedEvents = replay entityId (AsAt date)
        let state = recordedEvents |> reconstitute
        let! newEvents, cmd =
          aggregate.Exec state command
          |> TaskResult.mapError(DomainError)
        let commit events =
          repo.Commit entityId expectedVersion events
          |> TaskResult.mapError(EventSourcingError.Store)
        do! commit newEvents
        cmd |> Cmd.exec (printfn "Cmd failed %A") (List.singleton >> commit >> ignore)
        return newEvents
      }
    {
      Replay = replay
      Reconstitute = reconstitute
      Execute = execute
    }