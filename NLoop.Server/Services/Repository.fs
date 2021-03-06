﻿namespace NLoop.Server

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open NBitcoin

open System.IO
open System.Runtime.InteropServices
open System.Threading
open DBTrie.Storage.Cache
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Options
open FSharp.Control.Tasks
open DBTrie
open NBitcoin.Crypto
open NLoop.Domain
open NLoop.Domain.IO

module private DBKeys =
  [<Literal>]
  let HashToKey = "hk"

  [<Literal>]
  let HashToPreimage = "hp"

  [<Literal>]
  let idToLoopOutSwap = "io"

  [<Literal>]
  let idToLoopInSwap = "ii"

type IRepository =
  abstract member SetPrivateKey: key: Key -> Task
  abstract member GetPrivateKey: keyId: KeyId -> Task<Key option>
  abstract member SetPreimage: preimage: byte[] -> Task
  abstract member GetPreimage: preimageHashHash: uint160 -> Task<byte[] option>
  abstract member SetLoopOut: loopOut: LoopOut -> Task
  abstract member GetLoopOut: id: string -> Task<LoopOut option>
  abstract member SetLoopIn: loopIn: LoopIn -> Task
  abstract member GetLoopIn: id: string -> Task<LoopIn option>
  abstract member JsonOpts: JsonSerializerOptions

[<Sealed;AbstractClass;Extension>]
type IRepositoryExtensions() =
  [<Extension>]
  static member NewPrivateKey(this: IRepository) = task {
    let k = new Key()
    do! this.SetPrivateKey(k)
    return k
  }

  [<Extension>]
  static member NewPreimage(this: IRepository) = task {
    let preimage = RandomUtils.GetBytes(32)
    do! this.SetPreimage(preimage)
    return preimage
  }


type Repository(engine: DBTrieEngine, chainName: string, settings: ChainOptions, dbPath) =
  let jsonOpts = JsonSerializerOptions()

  do
    if dbPath |> Directory.Exists |> not then
      Directory.CreateDirectory(dbPath) |> ignore
    jsonOpts.AddNLoopJsonConverters(settings.GetNetwork(chainName))

  member this.SetPrivateKey(key: Key, [<O;DefaultParameterValue(null)>]ct: CancellationToken) =
    if (key |> box |> isNull) then raise <| ArgumentNullException(nameof key) else
    unitTask {
      use! tx = engine.OpenTransaction(ct)
      let k = ReadOnlyMemory(key.PubKey.Hash.ToBytes())
      let v = ReadOnlyMemory(key.ToBytes())
      let! _ = tx.GetTable(DBKeys.HashToKey).Insert(k, v)
      do! tx.Commit()
    }
  member this.GetPrivateKey(pubKeyHash: KeyId, [<O;DefaultParameterValue(null)>]ct: CancellationToken) =
    if (pubKeyHash |> box |> isNull) then raise <| ArgumentNullException(nameof pubKeyHash) else
    task {
      try
        use! tx = engine.OpenTransaction(ct)
        let k = pubKeyHash.ToBytes() |> ReadOnlyMemory
        let! row = tx.GetTable(DBKeys.HashToKey).Get(k)
        let! b = row.ReadValue()
        return new Key(b.ToArray()) |> Some
      with
      | _e -> return None
    }
  member this.SetPreimage(preimage: byte[], [<O;DefaultParameterValue(null)>]ct: CancellationToken) =
    if (preimage |> box |> isNull) then raise <| ArgumentNullException(nameof preimage) else
    if (preimage.Length <> 32) then raise <| ArgumentException($"length of {nameof preimage} must be 32") else
    unitTask {
      use! tx = engine.OpenTransaction(ct)
      let k = ReadOnlyMemory(preimage |> Hashes.Hash160 |> fun d -> d.ToBytes())
      let v = ReadOnlyMemory(preimage)
      let! _ = tx.GetTable(DBKeys.HashToKey).Insert(k, v)
      do! tx.Commit()
    }

  member this.GetPreimage(preimageHash: uint160, [<O;DefaultParameterValue(null)>]ct: CancellationToken) =
    if (preimageHash |> box |> isNull) then raise <| ArgumentNullException(nameof preimageHash) else
    task {
      try
        use! tx = engine.OpenTransaction(ct)
        let k = preimageHash.ToBytes() |> ReadOnlyMemory
        let! row = tx.GetTable(DBKeys.HashToKey).Get(k)
        let! x = row.ReadValue()
        return x.ToArray() |> Some
      with
      | _ -> return None
    }

    member this.SetLoopOut(loopOut: LoopOut) =
      if (loopOut |> box |> isNull) then raise <| ArgumentNullException(nameof loopOut) else
      unitTask {
        use! tx = engine.OpenTransaction()
        let v =
          let j = JsonSerializer.SerializeToUtf8Bytes(loopOut, jsonOpts)
          ReadOnlyMemory(j)
        let! _ = tx.GetTable(DBKeys.idToLoopOutSwap).Insert(loopOut.Id, v)
        do! tx.Commit()
      }
    member this.GetLoopOut(id: string) =
      if (id |> box |> isNull) then raise <| ArgumentNullException(nameof id) else
      task {
        try
          use! tx = engine.OpenTransaction()
          let! row = tx.GetTable(DBKeys.idToLoopOutSwap).Get(id)
          let! x = row.ReadValueString()
          return JsonSerializer.Deserialize<LoopOut>(x, jsonOpts) |> Some
        with
        | _e -> return None
      }
    member this.SetLoopIn(loopIn: LoopIn) =
      if (loopIn |> box |> isNull) then raise <| ArgumentNullException(nameof loopIn) else
      unitTask {
        use! tx = engine.OpenTransaction()
        let v =
          let j = JsonSerializer.SerializeToUtf8Bytes(loopIn, jsonOpts)
          ReadOnlyMemory(j)
        let! _ = tx.GetTable(DBKeys.idToLoopInSwap).Insert(loopIn.Id, v)
        do! tx.Commit()
      }
    member this.GetLoopIn(id: string) =
      if (id |> box |> isNull) then raise <| ArgumentNullException(nameof id) else
      task {
        try
          use! tx = engine.OpenTransaction()
          let! row = tx.GetTable(DBKeys.idToLoopInSwap).Get(id)
          match row with
          | null ->
            return None
          | r ->
            let! x = r.ReadValueString()
            return JsonSerializer.Deserialize<LoopIn>(x, jsonOpts) |> Some
        with
        | _e ->
          printfn "\n\n\nRepo: %A\n\n\n" _e
          return None
      }

    interface IRepository with
      member this.GetLoopIn(id) = this.GetLoopIn(id)
      member this.GetLoopOut(id) = this.GetLoopOut(id)
      member this.GetPreimage(preimageHashHash) = this.GetPreimage(preimageHashHash)
      member this.GetPrivateKey(keyId) = this.GetPrivateKey(keyId)
      member this.SetLoopIn(loopIn) = this.SetLoopIn(loopIn)
      member this.SetLoopOut(loopOut) = this.SetLoopOut(loopOut)
      member this.SetPreimage(preimage) = this.SetPreimage(preimage)
      member this.SetPrivateKey(key) = this.SetPrivateKey(key)
      member val JsonOpts = jsonOpts with get


type IRepositoryProvider =
  abstract member TryGetRepository: crypto: SupportedCryptoCode -> IRepository option

[<Extension;AbstractClass;Sealed>]
type IRepositoryProviderExtensions()=
  [<Extension>]
  static member GetRepository(this: IRepositoryProvider, crypto: SupportedCryptoCode): IRepository =
    match this.TryGetRepository crypto with
    | Some x -> x
    | None ->
      raise <| InvalidDataException($"cryptocode {crypto} not supported")

  [<Extension>]
  static member TryGetRepository(this: IRepositoryProvider, cryptoCode: string): IRepository option =
    cryptoCode |> SupportedCryptoCode.TryParse |> Option.bind this.TryGetRepository

  [<Extension>]
  static member GetRepository(this: IRepositoryProvider, cryptoCode: string): IRepository =
    match this.TryGetRepository(cryptoCode) with
    | Some x -> x
    | None ->
      raise <| InvalidDataException($"cryptocode {cryptoCode} not supported")
type RepositoryProvider(opts: IOptions<NLoopOptions>, logger: ILogger<RepositoryProvider>) =
  let repositories = Dictionary<SupportedCryptoCode, IRepository>()
  let startCompletion = TaskCompletionSource<bool>()

  let openEngine(dbPath) = task {
    return! DBTrieEngine.OpenFromFolder(dbPath)
  }

  let mutable engine = null
  let pageSize = 8192

  member this.StartCompletion = startCompletion.Task

  interface IRepositoryProvider with
    member this.TryGetRepository(crypto: SupportedCryptoCode): IRepository option =
      match repositories.TryGetValue(crypto) with
      | true, v -> Some v
      | false, _ -> None

  interface IHostedService with
    member this.StartAsync(_stoppingToken) = unitTask {
      logger.LogDebug($"Starting RepositoryProvider")
      try
        let dbPath = opts.Value.DBPath
        if (not <| Directory.Exists(dbPath)) then
          Directory.CreateDirectory(dbPath) |> ignore
        let! e = openEngine(dbPath)
        engine <- e
        engine.ConfigurePagePool(PagePool(pageSize, 50 * 1000 * 1000 / pageSize))
        for kv in opts.Value.ChainOptions do
          let repo =
            let dbPath = Path.Join(dbPath, kv.Key.ToString())
            Repository(engine, opts.Value.Network, kv.Value, dbPath)
          repositories.Add(kv.Key, repo)
        startCompletion.TrySetResult(true)
        |> ignore
      with
      | x ->
        startCompletion.TrySetCanceled() |> ignore
        raise <| x
    }

    member this.StopAsync(_cancellationToken) = unitTask {
      if (engine |> isNull |> not) then
        do! engine.DisposeAsync()
    }
