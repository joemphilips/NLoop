namespace NLoop.Server.Actors

open System
open System.Collections.Generic
open System.Threading.Channels
open FSharp.Control
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open DotNetLightning.Payment
open DotNetLightning.Utils
open EventStore.ClientAPI
open FSharp.Control
open FSharp.Control.Tasks
open FSharp.Control.Reactive
open FsToolkit.ErrorHandling
open LndClient
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NBitcoin
open NBitcoin.RPC
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Domain.Utils
open NLoop.Server
open NLoop.Server.Options
open NLoop.Server.DTOs
open NLoop.Server.SwapServerClient
open System.Reactive.Linq

[<AutoOpen>]
module internal SwapActorHelpers =
  let getSwapDeps b f g payInvoice payToAddress offer =
    { Swap.Deps.Broadcaster = b
      Swap.Deps.FeeEstimator = f
      Swap.Deps.GetRefundAddress = g
      Swap.Deps.PayInvoiceImmediate = payInvoice
      Swap.Deps.PayToAddress = payToAddress
      Swap.Deps.Offer = offer
      }

  let getObs (eventAggregator: IEventAggregator) (sId) =
      eventAggregator.GetObservable<Swap.EventWithId, Swap.ErrorWithId>()
      |> Observable.filter(function
                           | Choice1Of2 { Id = swapId }
                           | Choice2Of2 { Id = swapId } -> swapId = sId)


[<RequireQualifiedAccess>]
module Observable =
  let inline chooseOrError
    (selector: Swap.Event -> _ option)
    (obs: IObservable<Choice<Swap.EventWithId, Swap.ErrorWithId>>) =
      obs
      |> Observable.choose(
        function
        | Choice1Of2{ Event = Swap.Event.FinishedByError { Error = err } } -> err |> Error |> Some
        | Choice2Of2{ Error = DomainError e } -> e.Msg |> Error |> Some
        | Choice2Of2{ Error = Store(StoreError e) } -> e |> Error |> Some
        | Choice1Of2{ Event = e } -> selector e |> Option.map(Ok)
      )
      |> Observable.catchWith(fun ex -> Observable.Return(Error $"Error while handling observable {ex}"))
      |> Observable.first
      |> fun t -> t.GetAwaiter() |> Async.AwaitCSharpAwaitable

type SwapActor(opts: IOptions<NLoopOptions>,
               lightningClientProvider: ILightningClientProvider,
               broadcaster: IBroadcaster,
               feeEstimator: IFeeEstimator,
               eventAggregator: IEventAggregator,
               getAllSwapEvents: GetAllEvents<Swap.Event>,
               getRefundAddress: GetAddress,
               getWalletClient: GetWalletClient,
               store: Store,
               logger: ILogger<SwapActor>) =
  let aggr =
    let payInvoiceImmediate =
      fun (cc: SupportedCryptoCode) (param: Swap.PayInvoiceParams) (i: PaymentRequest) ->
        let req = {
          SendPaymentRequest.Invoice = i
          MaxFee = param.MaxFee
          OutgoingChannelIds = param.OutgoingChannelIds
          TimeoutSeconds = Constants.OfferTimeoutSeconds
        }
        task {
          match! lightningClientProvider.GetClient(cc).SendPayment(req) with
          | Ok r ->
            return {
              Swap.PayInvoiceResult.RoutingFee = r.Fee
              Swap.PayInvoiceResult.AmountPayed = i.AmountValue.Value
            }
          | Error e -> return raise <| exn $"Failed payment {e}"
        }
    let fundFromWallet =
      fun (req: WalletFundingRequest) -> task {
        let cli = getWalletClient(req.CryptoCode)
        let! txid = cli.FundToAddress(req.DestAddress, req.Amount, req.TargetConf)
        let blockchainCli = opts.Value.GetBlockChainClient(req.CryptoCode)
        return! blockchainCli.GetRawTransaction(TxId txid)
      }
    let offer =
      fun (cc: SupportedCryptoCode) (param: Swap.PayInvoiceParams) (i: PaymentRequest) ->
        let req = {
          SendPaymentRequest.Invoice = i
          MaxFee = param.MaxFee
          OutgoingChannelIds = param.OutgoingChannelIds
          TimeoutSeconds = Constants.OfferTimeoutSeconds
        }
        lightningClientProvider.GetClient(cc).Offer(req)
    getSwapDeps broadcaster feeEstimator getRefundAddress payInvoiceImmediate fundFromWallet offer
    |> Swap.getAggregate
  let mutable handler =
    Swap.getHandler aggr store

  /// We use queue to assure the change to the command execution is sequential.
  /// This is OK (since performance rarely be a consideration in swap) but it is
  /// not ideal in terms of performance, ideally we should allow a concurrent update
  /// and handle the StoreError (e.g. retry or abort)
  /// :todo:
  let workQueue = Channel.CreateBounded<SwapId * ESCommand<Swap.Command>> 10

  let _worker = task {
    let mutable finished = false
    while not <| finished do
      let! channelOpened = workQueue.Reader.WaitToReadAsync()
      finished <- not <| channelOpened
      if not finished then
        let! swapId, cmd = workQueue.Reader.ReadAsync()
        match! handler.Execute swapId cmd with
        | Ok events ->
          events
          |> List.iter(fun e ->
            eventAggregator.Publish e
            eventAggregator.Publish e.Data
            eventAggregator.Publish({ Swap.EventWithId.Id = swapId; Swap.EventWithId.Event = e.Data })
          )
        | Error (EventSourcingError.Store s as e) ->
          logger.LogError($"Store Error when executing the swap handler %A{s}")
          eventAggregator.Publish({ Swap.ErrorWithId.Id = swapId; Swap.ErrorWithId.Error = e })
          // todo: retry
          ()
        | Error s ->
          logger.LogError($"Error when executing swap handler %A{s}")
          eventAggregator.Publish({ Swap.ErrorWithId.Id = swapId; Swap.ErrorWithId.Error = s })
  }

  member val Handler = handler with get
  member val Aggregate = aggr with get
  member this.Execute(swapId, msg: Swap.Command, ?source) = unitTask {
    let source = defaultArg source (nameof(SwapActor))
    logger.LogDebug($"New Command {msg} for swap {swapId}: (source {source})")
    let cmd =
      { ESCommand.Data = msg
        Meta = { CommandMeta.Source = source
                 EffectiveDate = UnixDateTime.UtcNow } }

    let! channelOpened = workQueue.Writer.WaitToWriteAsync()
    if channelOpened then
      do! workQueue.Writer.WriteAsync((swapId, cmd))
  }

  interface ISwapActor with
    member this.Aggregate = this.Aggregate
    member this.Execute(i, cmd, s) =
      match s with
      | Some s -> this.Execute(i, cmd, s)
      | None -> this.Execute(i, cmd)

    member this.GetAllEntities(since, ?ct: CancellationToken) = task {
      let ct = defaultArg ct CancellationToken.None
      let! events = getAllSwapEvents since ct
      let eventListToStateMap (l: RecordedEvent<Swap.Event> list) =
        l
        |> List.groupBy(fun re -> re.StreamId)
        |> List.filter(fun (_streamId, reList) ->
          reList |> List.isEmpty |> not &&
            (reList.Head.Data.Type = Swap.new_loop_out_added || reList.Head.Data.Type = Swap.new_loop_in_added)
        )
        |> List.map(fun (streamId, reList) ->
          streamId,
          reList |> List.map(fun re -> re.AsEvent) |> this.Handler.Reconstitute
        )
        |> Map.ofList
      return
        events
        |> Result.map eventListToStateMap
    }
    member this.Handler = this.Handler

type ISwapExecutor =
  abstract member ExecNewLoopOut:
    req: LoopOutRequest *
    height: BlockHeight *
    ?s: string *
    ?ct: CancellationToken -> Task<Result<LoopOutResponse, string>>
  abstract member ExecNewLoopIn:
    req: LoopInRequest *
    height: BlockHeight *
    ?s: string *
    ?ct: CancellationToken -> Task<Result<LoopInResponse, string>>
type SwapExecutor(
                  invoiceProvider: ILightningInvoiceProvider,
                  opts: IOptions<NLoopOptions>,
                  logger: ILogger<SwapExecutor>,
                  eventAggregator: IEventAggregator,
                  swapServerClient: ISwapServerClient,
                  tryGetExchangeRate: TryGetExchangeRate,
                  lightningClientProvider: ILightningClientProvider,
                  getSwapKey: GetSwapKey,
                  getSwapPreimage: GetSwapPreimage,
                  getNetwork: GetNetwork,
                  getAddress: GetAddress,
                  swapActor: ISwapActor,
                  listeners: IEnumerable<ISwapEventListener>
  )=


  interface ISwapExecutor with
    /// Helper function for creating new loop out.
    /// Technically, the logic of this function should be in the Domain layer, but we want
    /// swapId to be the StreamId of the event stream, thus we have to
    /// get the `StreamId` outside of the Domain, So we must call `BoltzClient.CreateReverseSwap` and get the swapId before
    /// sending the command into the domain layer.
    /// If we define some internal UUID for swapid instead of using the one given by the boltz server, the logic of this
    /// function can go into the domain layer. But that complicates things by having two kinds of IDs for each swaps.
    member this.ExecNewLoopOut(
                               req: LoopOutRequest,
                               height: BlockHeight,
                               ?s,
                               ?ct
                               ) = taskResult {
        let s = defaultArg s (nameof(SwapExecutor))
        let ct = defaultArg ct CancellationToken.None
        let! claimKey = getSwapKey()
        let! preimage = getSwapPreimage()
        let preimageHash = preimage.Hash
        let pairId =
          req.PairIdValue

        let n = getNetwork(pairId.Base)
        let! outResponse =
          let req =
            { SwapDTO.LoopOutRequest.InvoiceAmount = req.Amount
              SwapDTO.LoopOutRequest.PairId = pairId
              SwapDTO.LoopOutRequest.ClaimPublicKey = claimKey.PubKey
              SwapDTO.LoopOutRequest.PreimageHash = preimageHash.Value }
          swapServerClient.LoopOut req

        ct.ThrowIfCancellationRequested()
        let! addr =
          match req.Address with
          | Some a -> TaskResult.retn a
          | None ->
            getAddress.Invoke(pairId.Base) |> TaskResult.map(fun a -> a.ToString())
        ct.ThrowIfCancellationRequested()
        let loopOut = {
          LoopOut.Id = outResponse.Id |> SwapId
          ClaimKey = claimKey
          OutgoingChanIds = req.OutgoingChannelIds
          Preimage = preimage
          RedeemScript = outResponse.RedeemScript
          Invoice = outResponse.Invoice.ToString()
          ClaimAddress = addr
          OnChainAmount = outResponse.OnchainAmount
          TimeoutBlockHeight = outResponse.TimeoutBlockHeight
          SwapTxHex = None
          ClaimTransactionId = None
          PairId = pairId
          ChainName = opts.Value.ChainName.ToString()
          Label = req.Label |> Option.defaultValue String.Empty
          PrepayInvoice =
            outResponse.MinerFeeInvoice
            |> Option.map(fun s -> s.ToString())
            |> Option.defaultValue String.Empty
          MaxMinerFee = req.Limits.MaxMinerFee
          SweepConfTarget =
            req.SweepConfTarget
            |> ValueOption.map(uint >> BlockHeightOffset32)
            |> ValueOption.defaultValue pairId.DefaultLoopOutParameters.SweepConfTarget
          IsClaimTxConfirmed = false
          IsOffchainOfferResolved = false
          Cost = SwapCost.Zero
          LoopOut.SwapTxConfRequirement =
            req.Limits.SwapTxConfRequirement
          SwapTxHeight = None
        }
        let! exchangeRate =
          tryGetExchangeRate(pairId, ct)
          |> Task.map(Result.requireSome $"exchange rate for {pairId} is not available")
        match outResponse.Validate(preimageHash.Value,
                                   claimKey.PubKey,
                                   req.Amount,
                                   req.Limits.MaxSwapFee,
                                   req.Limits.MaxPrepay,
                                   exchangeRate,
                                   n) with
        | Error e ->
          return! Error e
        | Ok () ->
          let group = {
            Swap.Group.Category = Swap.Category.Out
            Swap.Group.PairId = pairId
          }
          for l in listeners do
            l.RegisterSwap(loopOut.Id, group)
          let loopOutParams = {
            Swap.LoopOutParams.MaxPrepayFee = req.MaxPrepayRoutingFee |> ValueOption.defaultValue(Money.Coins 100000m)
            Swap.LoopOutParams.MaxPaymentFee = req.MaxSwapFee |> ValueOption.defaultValue(Money.Coins 100000m)
            Swap.LoopOutParams.Height = height
          }
          let obs =
            getObs eventAggregator loopOut.Id
            |> Observable.replay
          use _ = obs.Connect()
          do! swapActor.Execute(loopOut.Id, Swap.Command.NewLoopOut(loopOutParams, loopOut), s)
          let! maybeClaimTxId =
            let chooser =
              if loopOut.AcceptZeroConf then
                (function | Swap.Event.ClaimTxPublished { Txid = txId }  -> Some (Some txId) | _ -> None)
              else
                (function | Swap.Event.NewLoopOutAdded _  -> Some None | _ -> None)
            obs
            |> Observable.chooseOrError chooser
          return
            {
              LoopOutResponse.Id = loopOut.Id.Value
              Address = loopOut.ClaimAddress
              ClaimTxId = maybeClaimTxId
            }
    }
    member this.ExecNewLoopIn(loopIn: LoopInRequest, height: BlockHeight, ?source, ?ct) = taskResult {
        let ct = defaultArg ct CancellationToken.None
        let source = defaultArg source (nameof(SwapExecutor))
        let pairId =
          loopIn.PairIdValue
        let onChainNetwork = getNetwork(pairId.Quote)

        let! refundKey = getSwapKey()
        let! preimage = getSwapPreimage()

        let mutable maybeSwapId = None
        let lnClient = lightningClientProvider.GetClient(pairId.Base)
        let! channels = lnClient.ListChannels()
        let! maybeRouteHints =
          match loopIn.ChannelId with
          | Some cId ->
            lnClient.GetRouteHints(cId, ct)
            |> Task.map(Array.singleton)
          | None ->
            match loopIn.LastHop with
            | None -> Task.FromResult([||])
            | Some pk ->
              channels
              |> Seq.filter(fun c -> c.NodeId = pk)
              |> Seq.map(fun c ->
                // todo: do not add route hints for all possible channel.
                // Instead we should decide which channel is the one we want payment through.
                lnClient.GetRouteHints(c.Id, ct)
              )
              |> Task.WhenAll
        let! invoice =
          let amt = loopIn.Amount.ToLNMoney()
          let onPaymentFinished = fun (amt: Money) ->
            logger.LogInformation $"Received on-chain payment for loopIn swap {maybeSwapId}"
            match maybeSwapId with
            | Some i ->
              swapActor.Execute(i, Swap.Command.CommitReceivedOffChainPayment(amt), (nameof(invoiceProvider)))
            | None ->
              // This will never happen unless they pay us unconditionally.
              Task.CompletedTask

          let onPaymentCanceled = fun (msg: string) ->
            logger.LogWarning $"Invoice for the loopin swap {maybeSwapId} has been cancelled"
            match maybeSwapId with
            | Some i ->
              swapActor.Execute(i, Swap.Command.MarkAsErrored(msg), nameof(invoiceProvider))
            | None ->
              // This will never happen unless they pay us unconditionally.
              Task.CompletedTask
          invoiceProvider.GetAndListenToInvoice(
            pairId.Base,
            preimage,
            amt,
            loopIn.Label |> Option.defaultValue(String.Empty),
            maybeRouteHints,
            onPaymentFinished, onPaymentCanceled, None)

        ct.ThrowIfCancellationRequested()
        try
          let! inResponse =
            let req =
              { SwapDTO.LoopInRequest.Invoice = invoice
                SwapDTO.LoopInRequest.PairId = pairId
                SwapDTO.LoopInRequest.RefundPublicKey = refundKey.PubKey }
            swapServerClient.LoopIn req
          let swapId = inResponse.Id |> SwapId
          ct.ThrowIfCancellationRequested()
          maybeSwapId <- swapId |> Some
          let! exchangeRate =
            tryGetExchangeRate(pairId, ct)
            |> Task.map(Result.requireSome $"exchange rate for {PairId.toString(&pairId)} is not available")
          match inResponse.Validate(invoice.PaymentHash.Value,
                                    refundKey.PubKey,
                                    loopIn.Amount,
                                    loopIn.Limits.MaxSwapFee,
                                    onChainNetwork,
                                    exchangeRate) with
          | Error e ->
            do! swapActor.Execute(swapId, Swap.Command.MarkAsErrored(e), source)
            return! Error(e)
          | Ok addressType ->
            let group = {
              Swap.Group.Category = Swap.Category.In
              Swap.Group.PairId = pairId
            }
            for l in listeners do
              l.RegisterSwap(swapId, group)
            let loopIn = {
              LoopIn.Id = swapId
              RefundPrivateKey = refundKey
              Preimage = None
              RedeemScript = inResponse.RedeemScript
              Invoice = invoice.ToString()
              ExpectedAmount = inResponse.ExpectedAmount
              TimeoutBlockHeight = inResponse.TimeoutBlockHeight
              SwapTxInfoHex = None
              RefundTransactionId = None
              PairId = pairId
              ChainName = opts.Value.ChainName.ToString()
              Label = loopIn.Label |> Option.defaultValue String.Empty
              HTLCConfTarget =
                loopIn.HtlcConfTarget
                |> ValueOption.map(uint >> BlockHeightOffset32)
                |> ValueOption.defaultValue pairId.DefaultLoopInParameters.HTLCConfTarget
              Cost = SwapCost.Zero
              AddressType = addressType
              MaxMinerFee =
                loopIn.Limits.MaxMinerFee
              MaxSwapFee =
                loopIn.Limits.MaxSwapFee
              IsOffChainPaymentReceived = false
              IsOurSuccessTxConfirmed = false
              LastHop = maybeRouteHints.TryGetLastHop()
            }
            let obs =
              getObs(eventAggregator) swapId
              |> Observable.replay
            use _ = obs.Connect()
            do! swapActor.Execute(swapId, Swap.Command.NewLoopIn(height, loopIn), source)
            let! () =
              obs
              |> Observable.chooseOrError
                (function | Swap.Event.NewLoopInAdded _ -> Some () | _ -> None)
            return {
              LoopInResponse.Id = loopIn.Id.Value
              Address = loopIn.SwapAddress.ToString()
            }
        with
        | :? HttpRequestException as ex ->
          let msg = ex.Message.Replace("\"", "")
          return! Error($"Error requesting to boltz ({msg})")
      }
