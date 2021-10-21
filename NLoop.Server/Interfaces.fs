namespace NLoop.Server

open System.IO
open System.Runtime.CompilerServices
open LndClient
open NBitcoin
open FSharp.Control.Tasks
open NLoop.Domain

type ISwapEventListener =
  abstract member RegisterSwap: swapId: SwapId -> unit
  abstract member RemoveSwap: swapId: SwapId -> unit

type ILightningClientProvider =
  abstract member TryGetClient: crypto: SupportedCryptoCode -> INLoopLightningClient option
  abstract member GetAllClients: unit -> INLoopLightningClient seq

[<AbstractClass;Sealed;Extension>]
type ILightningClientProviderExtensions =
  [<Extension>]
  static member GetClient(this: ILightningClientProvider, crypto: SupportedCryptoCode) =
    match this.TryGetClient crypto with
    | Some v -> v
    | None -> raise <| InvalidDataException($"cryptocode {crypto} is not supported for layer 2")

  [<Extension>]
  static member AsChangeAddressGetter(this: ILightningClientProvider) =
    NLoop.Domain.IO.GetAddress(fun c ->
      task {
        match this.TryGetClient(c) with
        | None -> return Error("Unsupported Cryptocode")
        | Some s ->
          let! c = s.GetDepositAddress()
          return (Ok(c :> IDestination))
      }
    )
