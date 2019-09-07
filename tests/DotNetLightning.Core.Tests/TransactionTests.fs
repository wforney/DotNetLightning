module TransactionTests

open System
open System.Text.Json
open DotNetLightning.Crypto
open DotNetLightning.Serialize.Msgs
open DotNetLightning.Tests.Utils
open DotNetLightning.Transactions
open DotNetLightning.Utils
open System.IO
open Expecto
open Expecto.Logging
open Expecto.Logging.Message
open GeneratorsTests
open NBitcoin
open Secp256k1Net

(*
// let logger = Log.create "bolt3-transaction tests"
let logger = TestLogger.Create("bolt3-transaction tests")
let log = logger.LogSimple

let path = Path.Join(Directory.GetCurrentDirectory().AsSpan(), ("Data/bolt3-tx.json").AsSpan())
let data = JsonDocument.Parse(File.ReadAllText("Data/bolt3-tx.json"))

let localPerCommitmentPoint = PubKey("025f7117a78150fe2ef97db7cfc83bd57b2e2c0d0dd25eaf467a4a1c2a45ce1486")
let getLocal() =
    let ctx = new Secp256k1()
    let paymentBasePointSecret = uint256.Parse("1111111111111111111111111111111111111111111111111111111111111111")
    let paymentBasePoint = paymentBasePointSecret |> fun x -> x.ToBytes() |> Key
    let delayedPaymentBasePointSecret = uint256.Parse("3333333333333333333333333333333333333333333333333333333333333333")
    let delayedPaymentBasePoint = delayedPaymentBasePointSecret |> fun x -> x.ToBytes() |> Key
    {|
      Ctx = ctx
      CommitTxNumber = 42UL
      ToSelfDelay = 144us |> BlockHeightOffset
      DustLimit = Money.Satoshis(546L)
      PaymentBasePointSecret = paymentBasePointSecret
      PaymentBasePoint = paymentBasePoint
      RevocationBasePointSecret = uint256.Parse("2222222222222222222222222222222222222222222222222222222222222222")
      DelayedPaymentBasePointSecret = delayedPaymentBasePointSecret
      FundingPrivKey = "30ff4956bbdd3222d44cc5e8a1261dab1e07957bdac5ae88fe3261ef321f3749" |> hex.DecodeData |> Key
      PerCommitmentPoint = localPerCommitmentPoint
      PaymentPrivKey = Generators.derivePrivKey(ctx) (paymentBasePoint) (localPerCommitmentPoint)
      DelayedPaymentPrivKey = Generators.derivePrivKey(ctx) (delayedPaymentBasePoint) (localPerCommitmentPoint)
      RevocationPubKey = PubKey("0212a140cd0c6539d07cd08dfe09984dec3251ea808b892efeac3ede9402bf2b19")
      FeeRatePerKw = 15000u |> FeeRatePerKw
    |}

let getRemote() =
    let ctx = new Secp256k1()
    let paymentBasePointSecret = uint256.Parse    "4444444444444444444444444444444444444444444444444444444444444444"
    let paymentBasePoint = paymentBasePointSecret |> fun x -> x.ToBytes() |> Key
    let revocationBasePointSecret = uint256.Parse("2222222222222222222222222222222222222222222222222222222222222222")
    let revocationBasePoint = revocationBasePointSecret |> fun x -> x.ToBytes() |> Key
    {|
      Ctx = ctx
      CommitTxNumber = 42UL
      ToSelfDelay = 144us |> BlockHeightOffset
      DustLimit = Money.Satoshis(546L)
      PaymentBasePointSecret = paymentBasePointSecret
      PaymentBasePoint = paymentBasePoint
      RevocationBasePointSecret = revocationBasePointSecret
      RevocationBasePoint = revocationBasePoint
      FundingPrivKey = "1552dfba4f6cf29a62a0af13c8d6981d36d0ef8d61ba10fb0fe90da7634d7e13" |> hex.DecodeData |> Key
      PaymentPrivKey = Generators.derivePrivKey (ctx) (paymentBasePoint) localPerCommitmentPoint
      PerCommitmentPoint = "022c76692fd70814a8d1ed9dedc833318afaaed8188db4d14727e2e99bc619d325" |> uint256.Parse
    |}
    
let n = Network.RegTest
let coinbaseTx = Transaction.Parse("01000000010000000000000000000000000000000000000000000000000000000000000000ffffffff03510101ffffffff0100f2052a010000001976a9143ca33c2e4446f4a305f23c80df8ad1afdcf652f988ac00000000", n)

let fundingTx = Transaction.Parse("0200000001adbb20ea41a8423ea937e76e8151636bf6093b70eaff942930d20576600521fd000000006b48304502210090587b6201e166ad6af0227d3036a9454223d49a1f11839c1a362184340ef0240220577f7cd5cca78719405cbf1de7414ac027f0239ef6e214c90fcaab0454d84b3b012103535b32d5eb0a6ed0982a0479bbadc9868d9836f6ba94dd5a63be16d875069184ffffffff028096980000000000220020c015c4a6be010e21657068fc2e6a9d02b27ebe4d490a25846f7237f104d1a3cd20256d29010000001600143ca33c2e4446f4a305f23c80df8ad1afdcf652f900000000", n)
let fundingAmount = fundingTx.Outputs.[0].Value
log(sprintf "# funding-tx: %A" fundingTx)

let local = getLocal()
let remote = getRemote()
let fundingRedeem =
    let fundingPks = [| local.FundingPrivKey.PubKey
                        remote.FundingPrivKey.PubKey |]
    Scripts.multiSigOfM_2(true) (fundingPks)

let commitmentInputSCoin =
    Coin(fundingTx.GetHash(), 0u, fundingAmount, fundingRedeem.WitHash.ScriptPubKey)
    |> fun c -> ScriptCoin(c, fundingRedeem)

let obscuredTxNumber = Transactions.obscuredCommitTxNumber 42UL true local.FundingPrivKey.PubKey remote.FundingPrivKey.PubKey
assert(obscuredTxNumber = (0x2bb038521914UL ^^^ 42UL))

sprintf "local_payment_basepoint: %A " local.PaymentBasePoint |> log
sprintf "remote_payment_basepoint: %A" remote.PaymentBasePoint |> log
sprintf "local_funding_privkey: %A" local.FundingPrivKey |> log
sprintf "local_funding_pubkey: %A" local.FundingPrivKey.PubKey |> log
sprintf "remote_funding_privkey: %A" remote.FundingPrivKey |> log
sprintf "remote_funding_pubkey: %A" remote.FundingPrivKey.PubKey |> log
sprintf "local_secretkey: %A" local.PaymentPrivKey |> log
sprintf "localkey: %A" local.PaymentPrivKey.PubKey|> log
sprintf "remotekey: %A" remote.PaymentPrivKey |> log
sprintf "local_delayedkey: %A" local.DelayedPaymentPrivKey.PubKey |> log
sprintf "local_revocation_key: %A" local.RevocationPubKey|> log
sprintf "# funding wscript = %A" fundingRedeem |> log
assert(fundingRedeem = Script.FromBytesUnsafe(hex.DecodeData "5221023da092f6980e58d2c037173180e9a465476026ee50f96695963e8efe436f54eb21030e9f7b623d2ccc7c9bd44d66d5ce21ce504c0acf6385a132cec6d3c39fa711c152ae"))

let paymentPreImages =
    let _s = ([
            ("0000000000000000000000000000000000000000000000000000000000000000")
            ("0101010101010101010101010101010101010101010101010101010101010101")
            ("0202020202020202020202020202020202020202020202020202020202020202")
            ("0303030303030303030303030303030303030303030303030303030303030303")
            ("0404040404040404040404040404040404040404040404040404040404040404")
        ])
    _s |> List.map(hex.DecodeData) |> List.map(PaymentPreimage)
    
type h = DirectedHTLC
let htlcs = [
    { DirectedHTLC.Direction = In;
      Add = { UpdateAddHTLC.ChannelId = ChannelId.Zero;
              HTLCId = HTLCId.Zero;
              AmountMSat = LNMoney.MilliSatoshis(1000000L);
              PaymentHash = paymentPreImages.[0].GetSha256();
              CLTVExpiry = 500u |> BlockHeight;
              OnionRoutingPacket = OnionPacket.LastPacket } }
    { DirectedHTLC.Direction = In;
      Add = { UpdateAddHTLC.ChannelId = ChannelId.Zero;
              HTLCId = HTLCId.Zero;
              AmountMSat = LNMoney.MilliSatoshis(2000000L);
              PaymentHash = paymentPreImages.[1].GetSha256();
              CLTVExpiry = 501u |> BlockHeight;
              OnionRoutingPacket = OnionPacket.LastPacket } }
    { DirectedHTLC.Direction = Out;
      Add = { UpdateAddHTLC.ChannelId = ChannelId.Zero;
              HTLCId = HTLCId.Zero;
              AmountMSat = LNMoney.MilliSatoshis(2000000L);
              PaymentHash = paymentPreImages.[2].GetSha256();
              CLTVExpiry = 502u |> BlockHeight;
              OnionRoutingPacket = OnionPacket.LastPacket } }
    { DirectedHTLC.Direction = Out;
      Add = { UpdateAddHTLC.ChannelId = ChannelId.Zero;
              HTLCId = HTLCId.Zero;
              AmountMSat = LNMoney.MilliSatoshis(3000000L);
              PaymentHash = paymentPreImages.[3].GetSha256();
              CLTVExpiry = 503u |> BlockHeight;
              OnionRoutingPacket = OnionPacket.LastPacket } }
    { DirectedHTLC.Direction = In;
      Add = { UpdateAddHTLC.ChannelId = ChannelId.Zero;
              HTLCId = HTLCId.Zero;
              AmountMSat = LNMoney.MilliSatoshis(4000000L);
              PaymentHash = paymentPreImages.[4].GetSha256();
              CLTVExpiry = 503u |> BlockHeight;
              OnionRoutingPacket = OnionPacket.LastPacket } }
]

let htlcScripts =
    htlcs
    |> List.map(fun htlc -> match htlc.Direction with
                            | Out -> Scripts.htlcOffered
                                        local.PaymentPrivKey.PubKey
                                        (remote.PaymentPrivKey.PubKey)
                                        (local.RevocationPubKey)
                                        (htlc.Add.PaymentHash)
                            | In -> Scripts.htlcReceived
                                        (local.PaymentPrivKey.PubKey)
                                        (remote.PaymentPrivKey.PubKey)
                                        (local.RevocationPubKey)
                                        (htlc.Add.PaymentHash)
                                        (htlc.Add.CLTVExpiry.Value))
let run (spec: CommitmentSpec): (Transaction * _) =
    let local = getLocal()
    log (sprintf "to_local_msat %A" spec.ToLocal)
    log (sprintf "to_remote_msat %A" spec.ToRemote)
    log (sprintf "local_feerate_per_kw %A" spec.FeeRatePerKw)
    
    let commitTx =
        let commitTx = Transactions.makeCommitTx
                         (commitmentInputSCoin)
                         (local.CommitTxNumber)
                         (local.PaymentBasePoint.PubKey)
                         (remote.PaymentBasePoint.PubKey)
                         (true)
                         (local.DustLimit)
                         (local.RevocationPubKey)
                         (local.ToSelfDelay)
                         (local.DelayedPaymentPrivKey.PubKey)
                         (remote.PaymentPrivKey.PubKey)
                         (local.PaymentPrivKey.PubKey)
                         (remote.PaymentPrivKey.PubKey)
                         (spec)
                         (n)
        let localSig, tx2 = Transactions.sign(commitTx, local.FundingPrivKey)
        let remoteSig, tx3 = Transactions.sign(tx2, remote.FundingPrivKey)
        Transactions.checkSigAndAdd (tx3) (localSig) (local.FundingPrivKey.PubKey)
        >>= fun tx4 ->
            Transactions.checkSigAndAdd (tx4) (remoteSig) (remote.FundingPrivKey.PubKey)
        |> RResult.rderef
    let baseFee = Transactions.commitTxFee(local.DustLimit)(spec)
    log (sprintf "base commitment transaction fee is %A" baseFee)
    let actualFee = fundingAmount - match commitTx.Value.TryGetFee() with | true, f -> f | false, _ -> failwith ""
    log (sprintf "actual commitment tx fee is %A " actualFee)
    commitTx.Value.GetGlobalTransaction().Outputs
        |> List.ofSeq
        |> List.iter(fun txOut -> match txOut.ScriptPubKey.Length with
                                  | 22 -> log(sprintf "to-remote amount %A P2WPKH(%A)" (txOut.Value) (remote.PaymentPrivKey.PubKey))
                                  | 34 ->
                                      let maybeIndex = htlcScripts |> List.tryFindIndex(fun s -> s.WitHash.ScriptPubKey = txOut.ScriptPubKey)
                                      match maybeIndex with
                                      | None ->
                                          (sprintf "to-local amount %A. wscript (%A)"
                                               txOut.Value
                                               (Scripts.toLocalDelayed(local.RevocationPubKey)
                                                                      (local.ToSelfDelay)
                                                                      (local.DelayedPaymentPrivKey.PubKey)))
                                          |> log
                                      | Some i ->
                                          (sprintf "to-local amount %A wscript (%A)" txOut.Value htlcScripts.[i])
                                          |> log
                                  | x -> failwithf "unexpected scriptPubKey length %A" x)
    let actualCommitTxNum = Transactions.getCommitTxNumber
                                (commitTx.Value.GetGlobalTransaction())
                                (true)
                                (local.PaymentBasePoint.PubKey)
                                (remote.PaymentBasePoint.PubKey)
    let expectedCommitTxNumber = local.CommitTxNumber
    Expect.equal (expectedCommitTxNumber) (actualCommitTxNum) ""
    Expect.isTrue (commitTx.Value.CanExtractTransaction()) ""
    sprintf "output commit_tx %A" commitTx.Value |> log
    let (unsignedHTLCTimeoutTxs, unsignedHTLCSuccessTxs) =
        Transactions.makeHTLCTxs(commitTx.Value.ExtractTransaction())
                                 local.DustLimit
                                 local.RevocationPubKey
                                 local.ToSelfDelay
                                 local.DelayedPaymentPrivKey.PubKey
                                 local.PaymentPrivKey.PubKey
                                 remote.PaymentPrivKey.PubKey
                                 spec
                                 n
        |> RResult.rderef
    failwith ""

[<Tests>]
let tests =
    testList "Transaction test vectors" [
        ptestCase "simple commitment tx with no HTLCs" <| fun _ ->
            let spec = { CommitmentSpec.HTLCs = Map.empty; FeeRatePerKw = 15000u |> FeeRatePerKw;
                         ToLocal = LNMoney.MilliSatoshis(7000000000L); ToRemote =  3000000000L |> LNMoney.MilliSatoshis}
            let commitTx, htlcTxs = run(spec)
            Expect.equal(commitTx.Outputs.Count) (2) ""
            let expected = data.RootElement.TryGetProperty("simple commitment tx with no HTLCs")
                           |> function true, e -> e.ToString() | _ -> failwith ""
                           |> fun d -> Transaction.Parse(d, n)
            // Expect.equal expected commitTx ""
            ()
    ]
*)
