namespace NLoop.Server

open System
open System.Collections.Generic
open DotNetLightning.Utils
open FSharp.Control.Tasks
open System.Threading
open System.Linq
open NLoopLnClient
open NBitcoin
open NBitcoin.RPC

type RPCBlockchainClient(rpc: RPCClient) =
  interface IBlockChainClient with
    member this.GetBlock(blockHash, ?_ct) = task {
      let! resp = rpc.GetBlockAsync(blockHash, GetBlockVerbosity.WithFullTx)
      return
        {
          Height = resp.Height |> uint32 |> BlockHeight
          Block = resp.Block
        }
    }

    member this.GetBlockChainInfo(_ct) = task {
      let! resp = rpc.GetBlockchainInfoAsync()
      return {
        Progress = resp.VerificationProgress
        Height = resp.Blocks |> uint |> BlockHeight
        BestBlockHash = resp.BestBlockHash
      }
    }
    member this.GetBlockHash(height, _ct) =
      rpc.GetBlockHashAsync height.Value
    member this.GetRawTransaction(id, _ct) =
      rpc.GetRawTransactionAsync id.Value

    member this.GetBestBlockHash(_ct) =
      rpc.GetBestBlockHashAsync()

    member this.EstimateFee(target, _ct) = task {
      let! resp = rpc.EstimateSmartFeeAsync(target.Value |> int)
      return resp.FeeRate
    }
    member this.SendRawTransaction(tx, _ct) =
      rpc.SendRawTransactionAsync(tx)


type BitcoindWalletClient(rpc: RPCClient) =
  interface IWalletClient with
    member this.GetDepositAddress(_n, _ct) =
      let req = GetNewAddressRequest()
      req.AddressType <- Nullable(AddressType.P2SHSegwit)
      rpc.GetNewAddressAsync(req)

    member this.FundToAddress(dest, amount, confTarget, ct) =
      task {
        let ct = defaultArg ct CancellationToken.None
        let p = Dictionary<string, obj>()
        p.Add("address", dest.ToString());
        p.Add("amount", amount.ToDecimal(MoneyUnit.BTC))
        p.Add("comment", "nloop:FundToAddress")
        p.Add("conf_target", confTarget.Value |> int)
        let! resp = rpc.SendCommandWithNamedArgsAsync(RPCOperations.sendtoaddress.ToString(), p, ct).ConfigureAwait false
        return uint256.Parse(resp.Result.ToString())
      }

    member this.GetSendingTxFee(destinations, target, _ct) =
      task {
        let! fee = rpc.EstimateSmartFeeAsync(target.Value |> int)
        try
          let! psbt =
            let inputs = [||]
            let opts =
                let o = FundRawTransactionOptions()
                o.FeeRate <- fee.FeeRate
                o
            let d = destinations.ToDictionary((fun kv -> kv.Key), (fun kv -> kv.Value))
            rpc.WalletCreateFundedPSBTAsync(inputs, (d, Dictionary<_,_>()), LockTime.Zero, opts)
          return psbt.Fee |> Ok
        with
        | ex when ex.Message.Contains("insufficient funds", StringComparison.OrdinalIgnoreCase) ->
          return Error(WalletClientError.InSufficientFunds(ex.ToString()))
      }


