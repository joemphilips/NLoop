namespace NLoop.Domain.IO

open System.Threading.Tasks
open NBitcoin
open NLoop.Domain

type IFeeEstimator =
  abstract member Estimate: cryptoCode: SupportedCryptoCode -> Task<FeeRate>

type IBroadcaster =
  abstract member BroadcastTx: tx: Transaction * cryptoCode: SupportedCryptoCode -> Task

