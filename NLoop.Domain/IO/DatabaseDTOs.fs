namespace NLoop.Domain.IO

open System
open System.Text.Json.Serialization
open DotNetLightning.Payment
open DotNetLightning.Utils
open NBitcoin
open Newtonsoft.Json
open NLoop.Domain
open NLoop.Domain.Utils

type LoopOut = {
  [<JsonConverter(typeof<SwapIdJsonConverter>)>]
  Id: SwapId
  [<JsonConverter(typeof<SwapStatusTypeJsonConverter>)>]
  Status: SwapStatusType
  AcceptZeroConf: bool
  [<JsonConverter(typeof<PrivKeyJsonConverter>)>]
  ClaimKey: Key
  [<JsonConverter(typeof<PaymentPreimageJsonConverter>)>]
  Preimage: PaymentPreimage
  [<JsonConverter(typeof<ScriptJsonConverter>)>]
  RedeemScript: Script
  Invoice: string
  ClaimAddress: string
  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  OnChainAmount: Money
  [<JsonConverter(typeof<BlockHeightJsonConverter>)>]
  TimeoutBlockHeight: BlockHeight
  LockupTransactionId: uint256 option
  ClaimTransactionId: uint256 option
  [<JsonConverter(typeof<PairIdJsonConverter>)>]
  PairId: PairId
  ChainName: string
}
  with
  member this.OurNetwork =
    let struct (cryptoCode, _) = this.PairId
    cryptoCode.ToNetworkSet().GetNetwork(this.ChainName |> ChainName)
  member this.TheirNetwork =
    let struct (_, cryptoCode) = this.PairId
    cryptoCode.ToNetworkSet().GetNetwork(this.ChainName |> ChainName)

  member this.Validate() =
    if this.OnChainAmount <= Money.Zero then Error ("LoopOut has non-positive on chain amount") else
    Ok()

type LoopIn = {
  [<JsonConverter(typeof<SwapIdJsonConverter>)>]
  Id: SwapId
  [<JsonConverter(typeof<SwapStatusTypeJsonConverter>)>]
  Status: SwapStatusType

  [<JsonConverter(typeof<PrivKeyJsonConverter>)>]
  RefundPrivateKey: Key
  Preimage: PaymentPreimage option
  [<JsonConverter(typeof<ScriptJsonConverter>)>]
  RedeemScript: Script
  Invoice: string
  Address: string
  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  ExpectedAmount: Money
  [<JsonConverter(typeof<BlockHeightJsonConverter>)>]
  TimeoutBlockHeight: BlockHeight

  LockupTransactionHex: string option
  RefundTransactionId: uint256 option
  [<JsonConverter(typeof<PairIdJsonConverter>)>]
  PairId: PairId
  ChainName: string
}
  with
  member this.OurNetwork =
    let struct (cryptoCode, _) = this.PairId
    cryptoCode.ToNetworkSet().GetNetwork(this.ChainName |> ChainName)
  member this.TheirNetwork =
    let struct (_, cryptoCode) = this.PairId
    cryptoCode.ToNetworkSet().GetNetwork(this.ChainName |> ChainName)

  member this.Validate() =
    if this.ExpectedAmount <= Money.Zero then Error ($"LoopIn has non-positive expected amount {this.ExpectedAmount}") else
    Ok()


[<Struct>]
type AutoLoopRule = {
  Channel: ShortChannelId

  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  IncomingThreshold: Money

  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  OutgoingThreshold: Money
}

[<Measure>] type percent

/// minimum incoming and outgoing liquidity threshold
type ThresholdRule = {
  MinimumIncoming: int16<percent>
  MaximumIncoming: int16<percent>
}

/// Parameter for the swap. Which is global across channels.
type SwapParams = {
  // --- params form loop.liquidity.Parameters ---
  AutoLoop: bool
  AutoFeeBudget: Money
  /// max dispatched swap number
  MaxAutoInFlight: int
  /// The amount of time we wait before retry when swap fails
  FailureBackoff: TimeSpan

  SweepConfTarget: BlockHeight
  /// Fee limit for the swap.
  FeeLimit: FeeRate

  ClientRestrictions: {| MinimumSwapAmount: Money; MaximumSwapAmount: Money; |}
  ChannelRules: Map<ShortChannelId, ThresholdRule>
  PeerRules: Map<NodeId, ThresholdRule>

  // --- params from --setparams ---
  SweepLimit: FeeRate
  MaxMinerFee: Money
  MaxPrepayFee: float<percent>
  Budget: Money
}
