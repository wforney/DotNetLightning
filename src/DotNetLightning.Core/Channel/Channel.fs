namespace DotNetLightning.Channel

open DotNetLightning.Utils
open DotNetLightning.Utils.NBitcoinExtensions
open DotNetLightning.Utils.Aether
open DotNetLightning.Chain
open DotNetLightning.Crypto
open DotNetLightning.Transactions
open DotNetLightning.Serialization
open DotNetLightning.Serialization.Msgs
open NBitcoin
open System

open ResultUtils
open ResultUtils.Portability

type ChannelWaitingForFundingSigned = {
    ChannelOptions: ChannelOptions
    ChannelPrivKeys: ChannelPrivKeys
    FeeEstimator: IFeeEstimator
    RemoteNodeId: NodeId
    NodeSecret: NodeSecret
    Network: Network
    FundingTxMinimumDepth: BlockHeightOffset32
    ChannelId: ChannelId
    LocalParams: LocalParams
    RemoteParams: RemoteParams
    RemoteChannelPubKeys: ChannelPubKeys
    FundingTx: FinalizedTx
    LocalSpec: CommitmentSpec
    LocalCommitTx: CommitTx
    RemoteCommit: RemoteCommit
    ChannelFlags: uint8
    LastSent: FundingCreatedMsg
    LocalShutdownScriptPubKey: Option<ShutdownScriptPubKey>
    RemoteShutdownScriptPubKey: Option<ShutdownScriptPubKey>
} with
    member self.ApplyFundingSigned (msg: FundingSignedMsg)
                                       : Result<FinalizedTx * Channel, ChannelError> = result {
        let! finalizedLocalCommitTx =
            let theirFundingPk = self.RemoteChannelPubKeys.FundingPubKey.RawPubKey()
            let _, signedLocalCommitTx =
                self.ChannelPrivKeys.SignWithFundingPrivKey self.LocalCommitTx.Value
            let remoteSigPairOfLocalTx = (theirFundingPk,  TransactionSignature(msg.Signature.Value, SigHash.All))
            let sigPairs = seq [ remoteSigPairOfLocalTx; ]
            Transactions.checkTxFinalized signedLocalCommitTx self.LocalCommitTx.WhichInput sigPairs |> expectTransactionError
        let commitments = {
            IsFunder = true
            LocalParams = self.LocalParams
            RemoteParams = self.RemoteParams
            RemoteChannelPubKeys = self.RemoteChannelPubKeys
            ChannelFlags = self.ChannelFlags
            FundingScriptCoin =
                let amount =
                    let index = int self.LastSent.FundingOutputIndex.Value
                    self.FundingTx.Value.Outputs.[index].Value
                ChannelHelpers.getFundingScriptCoin
                    (self.ChannelPrivKeys.FundingPrivKey.FundingPubKey())
                    self.RemoteChannelPubKeys.FundingPubKey
                    self.LastSent.FundingTxId
                    self.LastSent.FundingOutputIndex
                    amount
            LocalCommit = {
                Index = CommitmentNumber.FirstCommitment
                Spec = self.LocalSpec
                PublishableTxs = {
                    PublishableTxs.CommitTx = finalizedLocalCommitTx
                    HTLCTxs = []
                }
                PendingHTLCSuccessTxs = []
            }
            RemoteCommit = self.RemoteCommit
            LocalChanges = LocalChanges.Zero
            RemoteChanges = RemoteChanges.Zero
            LocalNextHTLCId = HTLCId.Zero
            RemoteNextHTLCId = HTLCId.Zero
            OriginChannels = Map.empty
            RemotePerCommitmentSecrets = PerCommitmentSecretStore()
        }
        let nextState = ChannelState.Normal {
            ShortChannelId = None
            LocalShutdownState = ShutdownState.ForNewChannel self.LocalShutdownScriptPubKey
            RemoteShutdownState = ShutdownState.ForNewChannel self.RemoteShutdownScriptPubKey
            RemoteNextCommitInfo = None
        }
        let channel = {
            ChannelOptions = self.ChannelOptions
            ChannelPrivKeys = self.ChannelPrivKeys
            FeeEstimator = self.FeeEstimator
            RemoteNodeId = self.RemoteNodeId
            NodeSecret = self.NodeSecret
            State = nextState
            Network = self.Network
            FundingTxMinimumDepth = self.FundingTxMinimumDepth
            Commitments = commitments
        }
        return self.FundingTx, channel
    }

and ChannelWaitingForFundingCreated = {
    ChannelOptions: ChannelOptions
    ChannelPrivKeys: ChannelPrivKeys
    FeeEstimator: IFeeEstimator
    RemoteNodeId: NodeId
    NodeSecret: NodeSecret
    Network: Network
    FundingTxMinimumDepth: BlockHeightOffset32
    LocalShutdownScriptPubKey: Option<ShutdownScriptPubKey>
    RemoteShutdownScriptPubKey: Option<ShutdownScriptPubKey>
    TemporaryFailure: ChannelId
    LocalParams: LocalParams
    RemoteParams: RemoteParams
    RemoteChannelPubKeys: ChannelPubKeys
    FundingSatoshis: Money
    PushMSat: LNMoney
    InitialFeeRatePerKw: FeeRatePerKw
    RemoteFirstPerCommitmentPoint: PerCommitmentPoint
    ChannelFlags: uint8
} with
    member self.ApplyFundingCreated (msg: FundingCreatedMsg)
                                        : Result<FundingSignedMsg * Channel, ChannelError> = result {
        let! (localSpec, localCommitTx, remoteSpec, remoteCommitTx) =
            let firstPerCommitmentPoint =
                self.ChannelPrivKeys.CommitmentSeed.DerivePerCommitmentPoint
                    CommitmentNumber.FirstCommitment
            ChannelHelpers.makeFirstCommitTxs
                false
                (self.ChannelPrivKeys.ToChannelPubKeys())
                self.RemoteChannelPubKeys
                self.LocalParams
                self.RemoteParams
                self.FundingSatoshis
                self.PushMSat
                self.InitialFeeRatePerKw
                msg.FundingOutputIndex
                msg.FundingTxId
                firstPerCommitmentPoint
                self.RemoteFirstPerCommitmentPoint
                self.Network
        assert (localCommitTx.Value.IsReadyToSign())
        let _s, signedLocalCommitTx =
            self.ChannelPrivKeys.SignWithFundingPrivKey localCommitTx.Value
        let remoteTxSig = TransactionSignature(msg.Signature.Value, SigHash.All)
        let theirSigPair = (self.RemoteChannelPubKeys.FundingPubKey.RawPubKey(), remoteTxSig)
        let sigPairs = seq [ theirSigPair ]
        let! finalizedCommitTx =
            Transactions.checkTxFinalized (signedLocalCommitTx) (localCommitTx.WhichInput) sigPairs
            |> expectTransactionError
        let localSigOfRemoteCommit, _ =
            self.ChannelPrivKeys.SignWithFundingPrivKey remoteCommitTx.Value
        let commitments = {
            IsFunder = false
            LocalParams = self.LocalParams
            RemoteParams = self.RemoteParams
            RemoteChannelPubKeys = self.RemoteChannelPubKeys
            ChannelFlags = self.ChannelFlags
            FundingScriptCoin =
                ChannelHelpers.getFundingScriptCoin
                    (self.ChannelPrivKeys.FundingPrivKey.FundingPubKey())
                    self.RemoteChannelPubKeys.FundingPubKey
                    msg.FundingTxId
                    msg.FundingOutputIndex
                    self.FundingSatoshis
            LocalCommit = {
                Index = CommitmentNumber.FirstCommitment
                Spec = localSpec
                PublishableTxs = {
                    PublishableTxs.CommitTx = finalizedCommitTx
                    HTLCTxs = []
                }
                PendingHTLCSuccessTxs = []
            }
            RemoteCommit = {
                Index = CommitmentNumber.FirstCommitment
                Spec = remoteSpec
                TxId = remoteCommitTx.Value.GetGlobalTransaction().GetTxId()
                RemotePerCommitmentPoint = self.RemoteFirstPerCommitmentPoint
            }
            LocalChanges = LocalChanges.Zero
            RemoteChanges = RemoteChanges.Zero
            LocalNextHTLCId = HTLCId.Zero
            RemoteNextHTLCId = HTLCId.Zero
            OriginChannels = Map.empty
            RemotePerCommitmentSecrets = PerCommitmentSecretStore()
        }
        let channelId = commitments.ChannelId()
        let msgToSend: FundingSignedMsg = {
            ChannelId = channelId
            Signature = !>localSigOfRemoteCommit.Signature
        }
        let nextState = ChannelState.Normal {
            ShortChannelId = None
            LocalShutdownState = ShutdownState.ForNewChannel self.LocalShutdownScriptPubKey
            RemoteShutdownState = ShutdownState.ForNewChannel self.RemoteShutdownScriptPubKey
            RemoteNextCommitInfo = None
        }
        let channel = {
            ChannelOptions = self.ChannelOptions
            ChannelPrivKeys = self.ChannelPrivKeys
            FeeEstimator = self.FeeEstimator
            RemoteNodeId = self.RemoteNodeId
            NodeSecret = self.NodeSecret
            Network = self.Network
            State = nextState
            FundingTxMinimumDepth = self.FundingTxMinimumDepth
            Commitments = commitments
        }
        return msgToSend, channel
    }

and ChannelWaitingForFundingTx = {
    ChannelOptions: ChannelOptions
    ChannelPrivKeys: ChannelPrivKeys
    FeeEstimator: IFeeEstimator
    RemoteNodeId: NodeId
    NodeSecret: NodeSecret
    Network: Network
    LocalShutdownScriptPubKey: Option<ShutdownScriptPubKey>
    RemoteShutdownScriptPubKey: Option<ShutdownScriptPubKey>
    LastReceived: AcceptChannelMsg
    TemporaryChannelId: ChannelId
    RemoteChannelPubKeys: ChannelPubKeys
    FundingSatoshis: Money
    PushMSat: LNMoney
    InitFeeRatePerKw: FeeRatePerKw
    LocalParams: LocalParams
    RemoteInit: InitMsg
    ChannelFlags: uint8
} with
    member self.CreateFundingTx (fundingTx: FinalizedTx)
                                (outIndex: TxOutIndex)
                                    : Result<FundingCreatedMsg * ChannelWaitingForFundingSigned, ChannelError> = result {
        let remoteParams = RemoteParams.FromAcceptChannel self.RemoteInit self.LastReceived
        let localParams = self.LocalParams
        let commitmentSpec = CommitmentSpec.Create (self.FundingSatoshis.ToLNMoney() - self.PushMSat) self.PushMSat self.InitFeeRatePerKw
        let commitmentSeed = self.ChannelPrivKeys.CommitmentSeed
        let fundingTxId = fundingTx.Value.GetTxId()
        let! (_localSpec, localCommitTx, remoteSpec, remoteCommitTx) =
            ChannelHelpers.makeFirstCommitTxs
                true
                (self.ChannelPrivKeys.ToChannelPubKeys())
                self.RemoteChannelPubKeys
                localParams
                remoteParams
                self.FundingSatoshis
                self.PushMSat
                self.InitFeeRatePerKw
                outIndex
                fundingTxId
                (commitmentSeed.DerivePerCommitmentPoint CommitmentNumber.FirstCommitment)
                self.LastReceived.FirstPerCommitmentPoint
                self.Network
        let localSigOfRemoteCommit, _ =
            self.ChannelPrivKeys.SignWithFundingPrivKey remoteCommitTx.Value
        let nextMsg: FundingCreatedMsg = {
            TemporaryChannelId = self.LastReceived.TemporaryChannelId
            FundingTxId = fundingTxId
            FundingOutputIndex = outIndex
            Signature = !>localSigOfRemoteCommit.Signature
        }
        let channelId = OutPoint(fundingTxId.Value, uint32 outIndex.Value).ToChannelId()
        let channelWaitingForFundingSigned = {
            ChannelOptions = self.ChannelOptions
            ChannelPrivKeys = self.ChannelPrivKeys
            FeeEstimator = self.FeeEstimator
            RemoteNodeId = self.RemoteNodeId
            NodeSecret = self.NodeSecret
            Network = self.Network
            FundingTxMinimumDepth = self.LastReceived.MinimumDepth
            LocalShutdownScriptPubKey = self.LocalShutdownScriptPubKey
            RemoteShutdownScriptPubKey = self.RemoteShutdownScriptPubKey
            ChannelId = channelId
            LocalParams = localParams
            RemoteParams = remoteParams
            RemoteChannelPubKeys = self.RemoteChannelPubKeys
            FundingTx = fundingTx
            LocalSpec = commitmentSpec
            LocalCommitTx = localCommitTx
            RemoteCommit = {
                RemoteCommit.Index = CommitmentNumber.FirstCommitment
                Spec = remoteSpec
                TxId = remoteCommitTx.Value.GetGlobalTransaction().GetTxId()
                RemotePerCommitmentPoint = self.LastReceived.FirstPerCommitmentPoint
            }
            ChannelFlags = self.ChannelFlags
            LastSent = nextMsg
        }
        return nextMsg, channelWaitingForFundingSigned
    }


and ChannelWaitingForAcceptChannel = {
    ChannelOptions: ChannelOptions
    ChannelHandshakeLimits: ChannelHandshakeLimits
    ChannelPrivKeys: ChannelPrivKeys
    FeeEstimator: IFeeEstimator
    RemoteNodeId: NodeId
    NodeSecret: NodeSecret
    Network: Network
    LocalShutdownScriptPubKey: Option<ShutdownScriptPubKey>
    TemporaryChannelId: ChannelId
    FundingSatoshis: Money
    PushMSat: LNMoney
    InitFeeRatePerKw: FeeRatePerKw
    LocalParams: LocalParams
    RemoteInit: InitMsg
    ChannelFlags: uint8
} with
    member self.ApplyAcceptChannel (msg: AcceptChannelMsg)
                                       : Result<IDestination * Money * ChannelWaitingForFundingTx, ChannelError> = result {
        do! Validation.checkAcceptChannelMsgAcceptable self.ChannelHandshakeLimits self.FundingSatoshis self.LocalParams.ChannelReserveSatoshis self.LocalParams.DustLimitSatoshis msg
        let redeem =
            Scripts.funding
                (self.ChannelPrivKeys.ToChannelPubKeys().FundingPubKey)
                msg.FundingPubKey
        let remoteChannelPubKeys = {
            FundingPubKey = msg.FundingPubKey
            RevocationBasepoint = msg.RevocationBasepoint
            PaymentBasepoint = msg.PaymentBasepoint
            DelayedPaymentBasepoint = msg.DelayedPaymentBasepoint
            HtlcBasepoint = msg.HTLCBasepoint
        }
        let destination = redeem.WitHash :> IDestination
        let amount = self.FundingSatoshis
        let channelWaitingForFundingTx = {
            ChannelOptions = self.ChannelOptions
            ChannelPrivKeys = self.ChannelPrivKeys
            FeeEstimator = self.FeeEstimator
            RemoteNodeId = self.RemoteNodeId
            NodeSecret = self.NodeSecret
            Network = self.Network
            LocalShutdownScriptPubKey = self.LocalShutdownScriptPubKey
            RemoteShutdownScriptPubKey = msg.ShutdownScriptPubKey()
            LastReceived = msg
            TemporaryChannelId = self.TemporaryChannelId
            RemoteChannelPubKeys = remoteChannelPubKeys
            FundingSatoshis = self.FundingSatoshis
            PushMSat = self.PushMSat
            InitFeeRatePerKw = self.InitFeeRatePerKw
            LocalParams = self.LocalParams
            RemoteInit = self.RemoteInit
            ChannelFlags = self.ChannelFlags
        }
        return destination, amount, channelWaitingForFundingTx
    }

and Channel = {
    ChannelOptions: ChannelOptions
    ChannelPrivKeys: ChannelPrivKeys
    FeeEstimator: IFeeEstimator
    RemoteNodeId: NodeId
    NodeSecret: NodeSecret
    State: ChannelState
    Network: Network
    FundingTxMinimumDepth: BlockHeightOffset32
    Commitments: Commitments
 }
        with
        static member NewOutbound(channelHandshakeLimits: ChannelHandshakeLimits,
                                  channelOptions: ChannelOptions,
                                  nodeMasterPrivKey: NodeMasterPrivKey,
                                  channelIndex: int,
                                  feeEstimator: IFeeEstimator,
                                  network: Network,
                                  remoteNodeId: NodeId,
                                  shutdownScriptPubKey: Option<ShutdownScriptPubKey>,
                                  temporaryChannelId: ChannelId,
                                  fundingSatoshis: Money,
                                  pushMSat: LNMoney,
                                  initFeeRatePerKw: FeeRatePerKw,
                                  localParams: LocalParams,
                                  remoteInit: InitMsg,
                                  channelFlags: uint8,
                                  channelPrivKeys: ChannelPrivKeys
                                 ): Result<OpenChannelMsg * ChannelWaitingForAcceptChannel, ChannelError> =
            let openChannelMsgToSend: OpenChannelMsg = {
                Chainhash = network.Consensus.HashGenesisBlock
                TemporaryChannelId = temporaryChannelId
                FundingSatoshis = fundingSatoshis
                PushMSat = pushMSat
                DustLimitSatoshis = localParams.DustLimitSatoshis
                MaxHTLCValueInFlightMsat = localParams.MaxHTLCValueInFlightMSat
                ChannelReserveSatoshis = localParams.ChannelReserveSatoshis
                HTLCMinimumMsat = localParams.HTLCMinimumMSat
                FeeRatePerKw = initFeeRatePerKw
                ToSelfDelay = localParams.ToSelfDelay
                MaxAcceptedHTLCs = localParams.MaxAcceptedHTLCs
                FundingPubKey = channelPrivKeys.FundingPrivKey.FundingPubKey()
                RevocationBasepoint = channelPrivKeys.RevocationBasepointSecret.RevocationBasepoint()
                PaymentBasepoint = channelPrivKeys.PaymentBasepointSecret.PaymentBasepoint()
                DelayedPaymentBasepoint = channelPrivKeys.DelayedPaymentBasepointSecret.DelayedPaymentBasepoint()
                HTLCBasepoint = channelPrivKeys.HtlcBasepointSecret.HtlcBasepoint()
                FirstPerCommitmentPoint = channelPrivKeys.CommitmentSeed.DerivePerCommitmentPoint CommitmentNumber.FirstCommitment
                ChannelFlags = channelFlags
                TLVs = [| OpenChannelTLV.UpfrontShutdownScript shutdownScriptPubKey |]
            }
            result {
                do! Validation.checkOurOpenChannelMsgAcceptable openChannelMsgToSend
                let channelPrivKeys = nodeMasterPrivKey.ChannelPrivKeys channelIndex
                let nodeSecret = nodeMasterPrivKey.NodeSecret()
                let channelWaitingForAcceptChannel = {
                    ChannelHandshakeLimits = channelHandshakeLimits
                    ChannelOptions = channelOptions
                    ChannelPrivKeys = channelPrivKeys
                    FeeEstimator = feeEstimator
                    RemoteNodeId = remoteNodeId
                    NodeSecret = nodeSecret
                    Network = network
                    LocalShutdownScriptPubKey = shutdownScriptPubKey
                    TemporaryChannelId = temporaryChannelId
                    FundingSatoshis = fundingSatoshis
                    PushMSat = pushMSat
                    InitFeeRatePerKw = initFeeRatePerKw
                    LocalParams = localParams
                    RemoteInit = remoteInit
                    ChannelFlags = channelFlags
                }
                return (openChannelMsgToSend, channelWaitingForAcceptChannel)
            }

        static member NewInbound (channelHandshakeLimits: ChannelHandshakeLimits,
                                  channelOptions: ChannelOptions,
                                  nodeMasterPrivKey: NodeMasterPrivKey,
                                  channelIndex: int,
                                  feeEstimator: IFeeEstimator,
                                  network: Network,
                                  remoteNodeId: NodeId,
                                  minimumDepth: BlockHeightOffset32,
                                  shutdownScriptPubKey: Option<ShutdownScriptPubKey>,
                                  openChannelMsg: OpenChannelMsg,
                                  localParams: LocalParams,
                                  remoteInit: InitMsg,
                                  channelPrivKeys: ChannelPrivKeys
                                 ): Result<AcceptChannelMsg * ChannelWaitingForFundingCreated, ChannelError> =
            result {
                do! Validation.checkOpenChannelMsgAcceptable feeEstimator channelHandshakeLimits channelOptions openChannelMsg
                let firstPerCommitmentPoint = channelPrivKeys.CommitmentSeed.DerivePerCommitmentPoint CommitmentNumber.FirstCommitment
                let acceptChannelMsg: AcceptChannelMsg = {
                    TemporaryChannelId = openChannelMsg.TemporaryChannelId
                    DustLimitSatoshis = localParams.DustLimitSatoshis
                    MaxHTLCValueInFlightMsat = localParams.MaxHTLCValueInFlightMSat
                    ChannelReserveSatoshis = localParams.ChannelReserveSatoshis
                    HTLCMinimumMSat = localParams.HTLCMinimumMSat
                    MinimumDepth = minimumDepth
                    ToSelfDelay = localParams.ToSelfDelay
                    MaxAcceptedHTLCs = localParams.MaxAcceptedHTLCs
                    FundingPubKey = channelPrivKeys.FundingPrivKey.FundingPubKey()
                    RevocationBasepoint = channelPrivKeys.RevocationBasepointSecret.RevocationBasepoint()
                    PaymentBasepoint = channelPrivKeys.PaymentBasepointSecret.PaymentBasepoint()
                    DelayedPaymentBasepoint = channelPrivKeys.DelayedPaymentBasepointSecret.DelayedPaymentBasepoint()
                    HTLCBasepoint = channelPrivKeys.HtlcBasepointSecret.HtlcBasepoint()
                    FirstPerCommitmentPoint = firstPerCommitmentPoint
                    TLVs = [| AcceptChannelTLV.UpfrontShutdownScript shutdownScriptPubKey |]
                }
                let remoteChannelPubKeys = {
                    FundingPubKey = openChannelMsg.FundingPubKey
                    RevocationBasepoint = openChannelMsg.RevocationBasepoint
                    PaymentBasepoint = openChannelMsg.PaymentBasepoint
                    DelayedPaymentBasepoint = openChannelMsg.DelayedPaymentBasepoint
                    HtlcBasepoint = openChannelMsg.HTLCBasepoint
                }
                let remoteParams = RemoteParams.FromOpenChannel remoteInit openChannelMsg
                let channelPrivKeys = nodeMasterPrivKey.ChannelPrivKeys channelIndex
                let nodeSecret = nodeMasterPrivKey.NodeSecret()
                let channelWaitingForFundingCreated = {
                    ChannelOptions = channelOptions
                    ChannelPrivKeys = channelPrivKeys
                    FeeEstimator = feeEstimator
                    RemoteNodeId = remoteNodeId
                    NodeSecret = nodeSecret
                    Network = network
                    FundingTxMinimumDepth = minimumDepth
                    LocalShutdownScriptPubKey = shutdownScriptPubKey
                    RemoteShutdownScriptPubKey = openChannelMsg.ShutdownScriptPubKey()
                    ChannelFlags = openChannelMsg.ChannelFlags
                    TemporaryFailure = openChannelMsg.TemporaryChannelId
                    LocalParams = localParams
                    RemoteParams = remoteParams
                    RemoteChannelPubKeys = remoteChannelPubKeys
                    FundingSatoshis = openChannelMsg.FundingSatoshis
                    PushMSat = openChannelMsg.PushMSat
                    InitialFeeRatePerKw = openChannelMsg.FeeRatePerKw
                    RemoteFirstPerCommitmentPoint = openChannelMsg.FirstPerCommitmentPoint
                }
                return (acceptChannelMsg, channelWaitingForFundingCreated)
            }

module Channel =

    let private hex = NBitcoin.DataEncoders.HexEncoder()
    let private ascii = System.Text.ASCIIEncoding.ASCII
    let private dummyPrivKey = new Key(hex.DecodeData("0101010101010101010101010101010101010101010101010101010101010101"))
    let private dummyPubKey = dummyPrivKey.PubKey
    let private dummySig =
        "01010101010101010101010101010101" |> ascii.GetBytes
        |> uint256
        |> fun m -> dummyPrivKey.SignCompact(m)
        |> fun d -> LNECDSASignature.FromBytesCompact(d, true)
        |> fun ecdsaSig -> TransactionSignature(ecdsaSig.Value, SigHash.All)

    module Closing =
        let makeClosingTx (channelPrivKeys: ChannelPrivKeys,
                           cm: Commitments,
                           localSpk: ShutdownScriptPubKey,
                           remoteSpk: ShutdownScriptPubKey,
                           closingFee: Money,
                           network: Network
                          ) =
            let dustLimitSatoshis = Money.Max(cm.LocalParams.DustLimitSatoshis, cm.RemoteParams.DustLimitSatoshis)
            result {
                let! closingTx = Transactions.makeClosingTx (cm.FundingScriptCoin) (localSpk) (remoteSpk) (cm.IsFunder) (dustLimitSatoshis) (closingFee) (cm.LocalCommit.Spec) network
                let localSignature, psbtUpdated = channelPrivKeys.SignWithFundingPrivKey closingTx.Value
                let msg: ClosingSignedMsg = {
                    ChannelId = cm.ChannelId()
                    FeeSatoshis = closingFee
                    Signature = localSignature.Signature |> LNECDSASignature
                }
                return (ClosingTx psbtUpdated, msg)
            }

        let firstClosingFee (cm: Commitments)
                            (localSpk: ShutdownScriptPubKey)
                            (remoteSpk: ShutdownScriptPubKey)
                            (feeEst: IFeeEstimator)
                            (network: Network) =
            result {
                let! dummyClosingTx = Transactions.makeClosingTx cm.FundingScriptCoin localSpk remoteSpk cm.IsFunder Money.Zero Money.Zero cm.LocalCommit.Spec network
                let tx = dummyClosingTx.Value.GetGlobalTransaction()
                tx.Inputs.[0].WitScript <-
                    let witness = seq [ dummySig.ToBytes(); dummySig.ToBytes(); dummyClosingTx.Value.Inputs.[0].WitnessScript.ToBytes() ]
                    WitScript(witness)
                let feeRatePerKw = FeeRatePerKw.Max (feeEst.GetEstSatPer1000Weight(ConfirmationTarget.HighPriority), cm.LocalCommit.Spec.FeeRatePerKw)
                return feeRatePerKw.CalculateFeeFromVirtualSize(tx)
            }

        let makeFirstClosingTx (channelPrivKeys: ChannelPrivKeys,
                                commitments: Commitments,
                                localSpk: ShutdownScriptPubKey,
                                remoteSpk: ShutdownScriptPubKey,
                                feeEst: IFeeEstimator,
                                network: Network
                               ) =
            result {
                let! closingFee = firstClosingFee commitments localSpk remoteSpk feeEst network
                return! makeClosingTx (channelPrivKeys, commitments, localSpk, remoteSpk, closingFee, network)
            } |> expectTransactionError

        let nextClosingFee (localClosingFee: Money, remoteClosingFee: Money) =
            ((localClosingFee.Satoshi + remoteClosingFee.Satoshi) / 4L) * 2L
            |> Money.Satoshis

        let handleMutualClose (closingTx: FinalizedTx)
                              (_d: NegotiatingData)
                              (remoteNextCommitInfoOpt: Option<RemoteNextCommitInfo>)
                              (nextMessage: Option<ClosingSignedMsg>) =
            let nextData =
                ClosingData.Create
                    remoteNextCommitInfoOpt
            [ MutualClosePerformed (closingTx, nextData, nextMessage) ]
            |> Ok

        let claimCurrentLocalCommitTxOutputs (channelPrivKeys: ChannelPrivKeys,
                                              commitments: Commitments,
                                              commitTx: CommitTx
                                             ) =
            result {
                let commitmentSeed = channelPrivKeys.CommitmentSeed
                do! check (commitments.LocalCommit.PublishableTxs.CommitTx.Value.GetTxId()) (=) (commitTx.Value.GetTxId()) "txid mismatch. provided txid (%A) does not match current local commit tx (%A)"
                let _localPerCommitmentPoint =
                    commitmentSeed.DerivePerCommitmentPoint commitments.LocalCommit.Index
                failwith "TODO"
            }

    let makeChannelReestablish (channelPrivKeys: ChannelPrivKeys)
                               (commitments: Commitments)
                                   : Result<ChannelEvent list, ChannelError> =
        let commitmentSeed = channelPrivKeys.CommitmentSeed
        let ourChannelReestablish =
            {
                ChannelId = commitments.ChannelId()
                NextCommitmentNumber =
                    (commitments.RemotePerCommitmentSecrets.NextCommitmentNumber().NextCommitment())
                NextRevocationNumber =
                    commitments.RemotePerCommitmentSecrets.NextCommitmentNumber()
                DataLossProtect = OptionalField.Some <| {
                    YourLastPerCommitmentSecret =
                        commitments.RemotePerCommitmentSecrets.MostRecentPerCommitmentSecret()
                    MyCurrentPerCommitmentPoint =
                        commitmentSeed.DerivePerCommitmentPoint commitments.RemoteCommit.Index
                }
            }
        [ WeSentChannelReestablish ourChannelReestablish ] |> Ok

    let remoteNextCommitInfoIfFundingLocked (remoteNextCommitInfoOpt: Option<RemoteNextCommitInfo>)
                                            (operation: string)
                                                : Result<RemoteNextCommitInfo, ChannelError> =
        match remoteNextCommitInfoOpt with
        | None ->
            sprintf
                "cannot perform operation %s because peer has not sent funding_locked"
                operation
            |> apiMisuse
        | Some remoteNextCommitInfo -> Ok remoteNextCommitInfo

    let remoteNextCommitInfoIfFundingLockedNormal (normalData: NormalData)
                                                  (operation: string)
                                                      : Result<RemoteNextCommitInfo, ChannelError> =
        match normalData.ShortChannelId with
        | None ->
            sprintf
                "cannot perform operation %s because funding is not confirmed"
                operation
            |> apiMisuse
        | Some _ ->
            remoteNextCommitInfoIfFundingLocked normalData.RemoteNextCommitInfo operation

    let executeCommand (cs: Channel) (command: ChannelCommand): Result<ChannelEvent list, ChannelError> =
        match cs.State, command with

        // --------------- open channel procedure: case we are funder -------------
        | ChannelState.Normal _state, CreateChannelReestablish ->
            makeChannelReestablish cs.ChannelPrivKeys cs.Commitments
        | ChannelState.Normal state, ApplyFundingLocked msg ->
            result {
                do!
                    match state.RemoteNextCommitInfo with
                    | None -> Ok ()
                    | Some remoteNextCommitInfo ->
                        if remoteNextCommitInfo.PerCommitmentPoint() <> msg.NextPerCommitmentPoint then
                            Error <| InvalidFundingLocked { NetworkMsg = msg }
                        else
                            Ok ()
                match state.ShortChannelId with
                | None ->
                    return [ TheySentFundingLocked msg ]
                | Some _shortChannelId ->
                    let nextState = {
                        state with
                            RemoteNextCommitInfo =
                                Some <| RemoteNextCommitInfo.Revoked msg.NextPerCommitmentPoint
                    }
                    return [ BothFundingLocked(nextState) ]
            }
        | ChannelState.Normal state, ApplyFundingConfirmedOnBC(height, txindex, depth) ->
            match state.ShortChannelId with
            | None -> 
                if cs.FundingTxMinimumDepth > depth then
                    [] |> Ok
                else
                    let nextPerCommitmentPoint =
                        cs.ChannelPrivKeys.CommitmentSeed.DerivePerCommitmentPoint
                            (CommitmentNumber.FirstCommitment.NextCommitment())
                    let msgToSend: FundingLockedMsg = {
                        ChannelId = cs.Commitments.ChannelId()
                        NextPerCommitmentPoint = nextPerCommitmentPoint
                    }

                    // This is temporary channel id that we will use in our channel_update message, the goal is to be able to use our channel
                    // as soon as it reaches NORMAL state, and before it is announced on the network
                    // (this id might be updated when the funding tx gets deeply buried, if there was a reorg in the meantime)
                    // this is not specified in BOLT.
                    let shortChannelId = {
                        ShortChannelId.BlockHeight = height;
                        BlockIndex = txindex
                        TxOutIndex =
                            cs.Commitments.FundingScriptCoin.Outpoint.N
                            |> uint16
                            |> TxOutIndex
                    }
                    match state.RemoteNextCommitInfo with
                    | None ->
                        [ FundingConfirmed (msgToSend, shortChannelId) ] |> Ok
                    | Some remoteNextCommitInfo ->
                        let remoteNextPerCommitmentPoint =
                            match remoteNextCommitInfo with
                            | Revoked remoteNextPerCommitmentPoint -> remoteNextPerCommitmentPoint
                            // Note:
                            // We should never actually reach this line because we
                            // never send a commit before the funding is
                            // confirmed.
                            | Waiting remoteCommit -> remoteCommit.RemotePerCommitmentPoint
                        Ok [
                            FundingConfirmed (msgToSend, shortChannelId)
                            WeResumedDelayedFundingLocked remoteNextPerCommitmentPoint
                        ]
            | Some _shortChannelId ->
                if (cs.FundingTxMinimumDepth <= depth) then
                    [] |> Ok
                else
                    onceConfirmedFundingTxHasBecomeUnconfirmed(height, depth)

        // ---------- normal operation ---------
        | ChannelState.Normal state, AddHTLC op when state.HasEnteredShutdown() ->
            sprintf "Could not add new HTLC %A since shutdown is already in progress." op
            |> apiMisuse
        | ChannelState.Normal state, AddHTLC op ->
            result {
                do! Validation.checkOperationAddHTLC cs.Commitments op
                let add: UpdateAddHTLCMsg = {
                    ChannelId = cs.Commitments.ChannelId()
                    HTLCId = cs.Commitments.LocalNextHTLCId
                    Amount = op.Amount
                    PaymentHash = op.PaymentHash
                    CLTVExpiry = op.Expiry
                    OnionRoutingPacket = op.Onion
                }
                let commitments1 =
                    let commitments = {
                        cs.Commitments.AddLocalProposal(add) with
                            LocalNextHTLCId = cs.Commitments.LocalNextHTLCId + 1UL
                    }
                    match op.Origin with
                    | None -> commitments
                    | Some origin -> {
                        commitments with
                            OriginChannels =
                                cs.Commitments.OriginChannels
                                |> Map.add add.HTLCId origin
                    }

                let! remoteNextCommitInfo =
                    remoteNextCommitInfoIfFundingLockedNormal state "AddHTLC"
                // we need to base the next current commitment on the last sig we sent, even if we didn't yet receive their revocation
                let remoteCommit1 =
                    match remoteNextCommitInfo with
                    | Waiting nextRemoteCommit -> nextRemoteCommit
                    | Revoked _info -> commitments1.RemoteCommit
                let! reduced = remoteCommit1.Spec.Reduce(commitments1.RemoteChanges.ACKed, commitments1.LocalChanges.Proposed) |> expectTransactionError
                do! Validation.checkOurUpdateAddHTLCIsAcceptableWithCurrentSpec reduced commitments1 add
                return [ WeAcceptedOperationAddHTLC(add, commitments1) ]
            }
        | ChannelState.Normal _state, ApplyUpdateAddHTLC (msg, height) ->
            result {
                do! Validation.checkTheirUpdateAddHTLCIsAcceptable cs.Commitments msg height
                let commitments1 = {
                    cs.Commitments.AddRemoteProposal(msg) with
                        RemoteNextHTLCId = cs.Commitments.LocalNextHTLCId + 1UL
                }
                let! reduced =
                    commitments1.LocalCommit.Spec.Reduce (
                        commitments1.LocalChanges.ACKed,
                        commitments1.RemoteChanges.Proposed
                    ) |> expectTransactionError
                do! Validation.checkTheirUpdateAddHTLCIsAcceptableWithCurrentSpec reduced commitments1 msg
                return [ WeAcceptedUpdateAddHTLC commitments1 ]
            }

        | ChannelState.Normal state, FulfillHTLC cmd ->
            result {
                let! remoteNextCommitInfo =
                    remoteNextCommitInfoIfFundingLockedNormal state "FulfillHTLC"
                let! t = Commitments.sendFulfill cmd cs.Commitments remoteNextCommitInfo
                return [ WeAcceptedOperationFulfillHTLC t ]
            }

        | ChannelState.Normal state, ChannelCommand.ApplyUpdateFulfillHTLC msg ->
            result {
                let! remoteNextCommitInfo =
                    remoteNextCommitInfoIfFundingLockedNormal state "ApplyUpdateFulfullHTLC"
                return! Commitments.receiveFulfill msg cs.Commitments remoteNextCommitInfo
            }

        | ChannelState.Normal state, FailHTLC op ->
            result {
                let! remoteNextCommitInfo =
                    remoteNextCommitInfoIfFundingLockedNormal state "FailHTLC"
                return! Commitments.sendFail cs.NodeSecret op cs.Commitments remoteNextCommitInfo
            }

        | ChannelState.Normal state, FailMalformedHTLC op ->
            result {
                let! remoteNextCommitInfo =
                    remoteNextCommitInfoIfFundingLockedNormal state "FailMalformedHTLC"
                return! Commitments.sendFailMalformed op cs.Commitments remoteNextCommitInfo
            }

        | ChannelState.Normal state, ApplyUpdateFailHTLC msg ->
            result {
                let! remoteNextCommitInfo =
                    remoteNextCommitInfoIfFundingLockedNormal state "ApplyUpdateFailHTLC"
                return! Commitments.receiveFail msg cs.Commitments remoteNextCommitInfo
            }

        | ChannelState.Normal state, ApplyUpdateFailMalformedHTLC msg ->
            result {
                let! remoteNextCommitInfo =
                    remoteNextCommitInfoIfFundingLockedNormal state "ApplyUpdateFailMalformedHTLC"
                return! Commitments.receiveFailMalformed msg cs.Commitments remoteNextCommitInfo
            }

        | ChannelState.Normal state, UpdateFee op ->
            result {
                let! _remoteNextCommitInfo =
                    remoteNextCommitInfoIfFundingLockedNormal state "UpdateFee"
                return! cs.Commitments |> Commitments.sendFee op
            }
        | ChannelState.Normal state, ApplyUpdateFee msg ->
            result {
                let! _remoteNextCommitInfo =
                    remoteNextCommitInfoIfFundingLockedNormal state "ApplyUpdateFee"
                let localFeerate = cs.FeeEstimator.GetEstSatPer1000Weight(ConfirmationTarget.HighPriority)
                return! cs.Commitments |> Commitments.receiveFee cs.ChannelOptions localFeerate msg
            }

        | ChannelState.Normal state, SignCommitment ->
            let cm = cs.Commitments
            result {
                let! remoteNextCommitInfo =
                    remoteNextCommitInfoIfFundingLockedNormal state "SignCommit"
                match remoteNextCommitInfo with
                | _ when (cm.LocalHasChanges() |> not) ->
                    // Ignore SignCommitment Command (nothing to sign)
                    return []
                | RemoteNextCommitInfo.Revoked _ ->
                    return! Commitments.sendCommit cs.ChannelPrivKeys cs.Network cm remoteNextCommitInfo
                | RemoteNextCommitInfo.Waiting _ ->
                    // Already in the process of signing
                    return []
            }

        | ChannelState.Normal state, ApplyCommitmentSigned msg ->
            result {
                let! _remoteNextCommitInfo =
                    remoteNextCommitInfoIfFundingLockedNormal state "ApplyCommitmentSigned"
                return! cs.Commitments |> Commitments.receiveCommit cs.ChannelPrivKeys msg cs.Network
            }

        | ChannelState.Normal state, ApplyRevokeAndACK msg ->
            result {
                let! remoteNextCommitInfo =
                    remoteNextCommitInfoIfFundingLockedNormal state "ApplyRevokeAndACK"
                let cm = cs.Commitments
                match remoteNextCommitInfo with
                | RemoteNextCommitInfo.Waiting _ when (msg.PerCommitmentSecret.PerCommitmentPoint() <> cm.RemoteCommit.RemotePerCommitmentPoint) ->
                    let errorMsg = sprintf "Invalid revoke_and_ack %A; must be %A" msg.PerCommitmentSecret cm.RemoteCommit.RemotePerCommitmentPoint
                    return! Error <| invalidRevokeAndACK msg errorMsg
                | RemoteNextCommitInfo.Revoked _ ->
                    let errorMsg = sprintf "Unexpected revocation"
                    return! Error <| invalidRevokeAndACK msg errorMsg
                | RemoteNextCommitInfo.Waiting theirNextCommit ->
                    let remotePerCommitmentSecretsOpt =
                        cm.RemotePerCommitmentSecrets.InsertPerCommitmentSecret
                            cm.RemoteCommit.Index
                            msg.PerCommitmentSecret
                    match remotePerCommitmentSecretsOpt with
                    | Error err -> return! Error <| invalidRevokeAndACK msg err.Message
                    | Ok remotePerCommitmentSecrets ->
                        let commitments1 = {
                            cm with
                                LocalChanges = {
                                    cm.LocalChanges with
                                        Signed = [];
                                        ACKed = cm.LocalChanges.ACKed @ cm.LocalChanges.Signed
                                }
                                RemoteChanges = {
                                    cm.RemoteChanges with
                                        Signed = []
                                }
                                RemoteCommit = theirNextCommit
                                RemotePerCommitmentSecrets = remotePerCommitmentSecrets
                        }
                        return [ WeAcceptedRevokeAndACK(commitments1, msg.NextPerCommitmentPoint) ]
            }

        | ChannelState.Normal state, ChannelCommand.Close localShutdownScriptPubKey ->
            result {
                match state.LocalShutdownState with
                | Some shutdownState when shutdownState.HasRequestedShutdown ->
                    do! Error <| cannotCloseChannel "shutdown is already in progress"
                | _ -> ()
                let! nextLocalShutdownState =
                    Validation.checkShutdownScriptPubKeyAcceptable
                        state.LocalShutdownState
                        localShutdownScriptPubKey
                if (cs.Commitments.LocalHasUnsignedOutgoingHTLCs()) then
                    do! Error <| cannotCloseChannel "Cannot close with unsigned outgoing htlcs"
                let shutdownMsg: ShutdownMsg = {
                    ChannelId = cs.Commitments.ChannelId()
                    ScriptPubKey = localShutdownScriptPubKey
                }
                return [ AcceptedOperationShutdown(shutdownMsg, nextLocalShutdownState) ]
            }
        | ChannelState.Normal state, RemoteShutdown(msg, localShutdownScriptPubKey) ->
            result {
                let! localShutdownState =
                    Validation.checkShutdownScriptPubKeyAcceptable
                        state.LocalShutdownState
                        localShutdownScriptPubKey
                let! remoteShutdownState =
                    Validation.checkShutdownScriptPubKeyAcceptable
                        state.RemoteShutdownState
                        msg.ScriptPubKey
                let cm = cs.Commitments
                // They have pending unsigned htlcs => they violated the spec, close the channel
                // they don't have pending unsigned htlcs
                //      We have pending unsigned htlcs
                //          We already sent a shutdown msg => spec violation (we can't send htlcs after having sent shutdown)
                //          We did not send a shutdown msg
                //              We are ready to sign => we stop sending further htlcs, we initiate a signature
                //              We are waiting for a rev => we stop sending further htlcs, we wait for their revocation, will resign immediately after, and then we will send our shutdown msg
                //      We have no pending unsigned htlcs
                //          we already sent a shutdown msg
                //              There are pending signed htlcs => send our shutdown msg, go to SHUTDOWN state
                //              there are no htlcs => send our shutdown msg, goto NEGOTIATING state
                //          We did not send a shutdown msg
                //              There are pending signed htlcs => go to SHUTDOWN state
                //              there are no HTLCs => go to NEGOTIATING state
                
                if (cm.RemoteHasUnsignedOutgoingHTLCs()) then
                    return! receivedShutdownWhenRemoteHasUnsignedOutgoingHTLCs msg
                // Do we have Unsigned Outgoing HTLCs?
                else if (cm.LocalHasUnsignedOutgoingHTLCs()) then
                    let remoteNextCommitInfo =
                        match state.RemoteNextCommitInfo with
                        | None -> failwith "can't have unsigned outgoing htlcs if we haven't recieved funding_locked. this should never happen"
                        | Some remoteNextCommitInfo -> remoteNextCommitInfo
                    // Are we in the middle of a signature?
                    match remoteNextCommitInfo with
                    // yes.
                    | RemoteNextCommitInfo.Waiting _waitingForRevocation ->
                        return [
                            AcceptedShutdownWhileWeHaveUnsignedOutgoingHTLCs remoteShutdownState
                        ]
                    // No. let's sign right away.
                    | RemoteNextCommitInfo.Revoked _ ->
                        return [
                            ChannelStateRequestedSignCommitment;
                            AcceptedShutdownWhileWeHaveUnsignedOutgoingHTLCs remoteShutdownState
                        ]
                else
                    let hasNoPendingHTLCs =
                        match state.RemoteNextCommitInfo with
                        | None -> true
                        | Some remoteNextCommitInfo -> cm.HasNoPendingHTLCs remoteNextCommitInfo
                    if hasNoPendingHTLCs then
                        // we have to send first closing_signed msg iif we are the funder
                        if (cm.IsFunder) then
                            let! (closingTx, closingSignedMsg) =
                                Closing.makeFirstClosingTx (
                                    cs.ChannelPrivKeys,
                                    cm,
                                    localShutdownScriptPubKey,
                                    msg.ScriptPubKey,
                                    cs.FeeEstimator,
                                    cs.Network
                                )
                            let nextState = {
                                RemoteNextCommitInfo = state.RemoteNextCommitInfo
                                LocalShutdown = localShutdownScriptPubKey
                                RemoteShutdown = msg.ScriptPubKey
                                ClosingTxProposed = [{
                                    ClosingTxProposed.UnsignedTx = closingTx
                                    LocalClosingSigned = closingSignedMsg
                                }]
                                MaybeBestUnpublishedTx = None
                            }
                            return [
                                AcceptedShutdownWhenNoPendingHTLCs(
                                    closingSignedMsg |> Some,
                                    nextState
                                )
                            ]
                        else
                            let nextState = {
                                RemoteNextCommitInfo = state.RemoteNextCommitInfo
                                LocalShutdown = localShutdownScriptPubKey
                                RemoteShutdown = msg.ScriptPubKey
                                ClosingTxProposed = []
                                MaybeBestUnpublishedTx = None
                            }
                            return [ AcceptedShutdownWhenNoPendingHTLCs(None, nextState) ]
                    else
                        let localShutdownMsg: ShutdownMsg = {
                            ChannelId = cs.Commitments.ChannelId()
                            ScriptPubKey = localShutdownScriptPubKey
                        }
                        let nextState = {
                            state with
                                LocalShutdownState = Some localShutdownState
                                RemoteShutdownState = Some remoteShutdownState
                        }
                        return [
                            AcceptedShutdownWhenWeHavePendingHTLCs(
                                localShutdownMsg,
                                nextState
                            )
                        ]
            }
        // ----------- closing ---------
        | Negotiating state, ApplyClosingSigned msg ->
            result {
                let cm = cs.Commitments
                let remoteChannelKeys = cm.RemoteChannelPubKeys
                let lastCommitFeeSatoshi =
                    cm.FundingScriptCoin.TxOut.Value - (cm.LocalCommit.PublishableTxs.CommitTx.Value.TotalOut)
                do! checkRemoteProposedHigherFeeThanBefore lastCommitFeeSatoshi msg.FeeSatoshis
                let! closingTx, closingSignedMsg =
                    Closing.makeClosingTx (
                        cs.ChannelPrivKeys,
                        cm,
                        state.LocalShutdown,
                        state.RemoteShutdown,
                        msg.FeeSatoshis,
                        cs.Network
                    )
                    |> expectTransactionError
                let! finalizedTx =
                    Transactions.checkTxFinalized
                        closingTx.Value
                        closingTx.WhichInput
                        (seq [
                            remoteChannelKeys.FundingPubKey.RawPubKey(),
                            TransactionSignature(msg.Signature.Value, SigHash.All)
                        ])
                    |> expectTransactionError
                let maybeLocalFee =
                    state.ClosingTxProposed
                    |> List.tryHead
                    |> Option.map (fun v -> v.LocalClosingSigned.FeeSatoshis)
                let areWeInDeal = Some(msg.FeeSatoshis) = maybeLocalFee
                let hasTooManyNegotiationDone =
                    (state.ClosingTxProposed |> List.length) >= cs.ChannelOptions.MaxClosingNegotiationIterations
                if (areWeInDeal || hasTooManyNegotiationDone) then
                    return!
                        Closing.handleMutualClose
                            finalizedTx
                            { state with MaybeBestUnpublishedTx = Some(finalizedTx) }
                            state.RemoteNextCommitInfo
                            None
                else
                    let lastLocalClosingFee = state.ClosingTxProposed |> List.tryHead |> Option.map (fun txp -> txp.LocalClosingSigned.FeeSatoshis)
                    let! localF = 
                        match lastLocalClosingFee with
                        | Some v -> Ok v
                        | None ->
                            Closing.firstClosingFee
                                cs.Commitments
                                state.LocalShutdown
                                state.RemoteShutdown
                                cs.FeeEstimator
                                cs.Network
                            |> expectTransactionError
                    let nextClosingFee =
                        Closing.nextClosingFee (localF, msg.FeeSatoshis)
                    if (Some nextClosingFee = lastLocalClosingFee) then
                        return!
                            Closing.handleMutualClose
                                finalizedTx
                                { state with MaybeBestUnpublishedTx = Some(finalizedTx) }
                                state.RemoteNextCommitInfo
                                None
                    else if (nextClosingFee = msg.FeeSatoshis) then
                        // we have reached on agreement!
                        let closingTxProposed1 =
                            let newProposed = {
                                ClosingTxProposed.UnsignedTx = closingTx
                                LocalClosingSigned = closingSignedMsg
                            }
                            newProposed :: state.ClosingTxProposed
                        let negoData = { state with ClosingTxProposed = closingTxProposed1
                                                    MaybeBestUnpublishedTx = Some(finalizedTx) }
                        return!
                            Closing.handleMutualClose
                                finalizedTx
                                negoData
                                state.RemoteNextCommitInfo
                                (Some closingSignedMsg)
                    else
                        let! closingTx, closingSignedMsg =
                            Closing.makeClosingTx (
                                cs.ChannelPrivKeys,
                                cm,
                                state.LocalShutdown,
                                state.RemoteShutdown,
                                nextClosingFee,
                                cs.Network
                            )
                            |> expectTransactionError
                        let closingTxProposed1 =
                            let newProposed = {
                                ClosingTxProposed.UnsignedTx = closingTx
                                LocalClosingSigned = closingSignedMsg
                            }
                            newProposed :: state.ClosingTxProposed
                        let nextState = { state with ClosingTxProposed = closingTxProposed1; MaybeBestUnpublishedTx = Some(finalizedTx) }
                        return [ WeProposedNewClosingSigned(closingSignedMsg, nextState) ]
            }
        | Closing state, FulfillHTLC op ->
            // got valid payment preimage, recalculating txs to redeem the corresponding htlc on-chain
            result {
                let cm = cs.Commitments
                let! remoteNextCommitInfo =
                    remoteNextCommitInfoIfFundingLocked
                        state.RemoteNextCommitInfo
                        "FulfillHTC"
                let! (_msgToSend, _newCommitments) =
                    Commitments.sendFulfill op cm remoteNextCommitInfo
                return failwith "Not Implemented yet"
            }
        | state, cmd ->
            undefinedStateAndCmdPair state cmd

    let applyEvent c (e: ChannelEvent): Channel =
        match e, c.State with
        // --------- init both ------
        | FundingConfirmed (_ourFundingLockedMsg, shortChannelId), ChannelState.Normal normalData ->
            {
                c with
                    State = ChannelState.Normal {
                        normalData with
                            ShortChannelId = Some shortChannelId
                    }
            }
        | TheySentFundingLocked msg, ChannelState.Normal normalData ->
            {
                c with
                    State = ChannelState.Normal {
                        normalData with
                            RemoteNextCommitInfo =
                                Some <| RemoteNextCommitInfo.Revoked msg.NextPerCommitmentPoint
                    }
            }
        | BothFundingLocked data, ChannelState.Normal _normalData ->
            {
                c with
                    State = ChannelState.Normal data
            }
        // ----- normal operation --------
        | WeAcceptedOperationAddHTLC(_, newCommitments), ChannelState.Normal _normalData ->
            { c with Commitments = newCommitments }
        | WeAcceptedUpdateAddHTLC(newCommitments), ChannelState.Normal _normalData ->
            { c with Commitments = newCommitments }

        | WeAcceptedOperationFulfillHTLC(_, newCommitments), ChannelState.Normal _normalData ->
            { c with Commitments = newCommitments }
        | WeAcceptedFulfillHTLC(_msg, _origin, _htlc, newCommitments), ChannelState.Normal _normalData ->
            { c with Commitments = newCommitments }

        | WeAcceptedOperationFailHTLC(_msg, newCommitments), ChannelState.Normal _normalData ->
            { c with Commitments = newCommitments }
        | WeAcceptedFailHTLC(_origin, _msg, newCommitments), ChannelState.Normal _normalData ->
            { c with Commitments = newCommitments }

        | WeAcceptedOperationFailMalformedHTLC(_msg, newCommitments), ChannelState.Normal _normalData ->
            { c with Commitments = newCommitments }
        | WeAcceptedFailMalformedHTLC(_origin, _msg, newCommitments), ChannelState.Normal _normalData ->
            { c with Commitments = newCommitments }

        | WeAcceptedOperationUpdateFee(_msg, newCommitments), ChannelState.Normal _normalData ->
            { c with Commitments = newCommitments }
        | WeAcceptedUpdateFee(_msg, newCommitments), ChannelState.Normal _normalData ->
            { c with Commitments = newCommitments }

        | WeAcceptedOperationSign(_msg, newCommitments, waitingForRevocation), ChannelState.Normal normalData ->
            {
                c with
                    Commitments = newCommitments
                    State = ChannelState.Normal {
                        normalData with
                            RemoteNextCommitInfo =
                                Some <| RemoteNextCommitInfo.Waiting waitingForRevocation
                    }
            }
        | WeAcceptedCommitmentSigned(_msg, newCommitments), ChannelState.Normal _normalData ->
            { c with Commitments = newCommitments }

        | WeAcceptedRevokeAndACK(newCommitments, remoteNextPerCommitmentPoint), ChannelState.Normal normalData ->
            {
                c with
                    Commitments = newCommitments
                    State = ChannelState.Normal {
                        normalData with
                            RemoteNextCommitInfo =
                                Some <| RemoteNextCommitInfo.Revoked remoteNextPerCommitmentPoint
                    }
            }

        // -----  closing ------
        | AcceptedOperationShutdown(_msg, nextLocalShutdownState), ChannelState.Normal normalData ->
            {
                c with
                    State = ChannelState.Normal {
                        normalData with
                            LocalShutdownState = Some nextLocalShutdownState
                    }
            }
        | AcceptedShutdownWhileWeHaveUnsignedOutgoingHTLCs nextRemoteShutdownState, ChannelState.Normal normalData ->
            { 
                c with
                    State = ChannelState.Normal {
                        normalData with
                            RemoteShutdownState = Some nextRemoteShutdownState
                    }
            }
        | AcceptedShutdownWhenNoPendingHTLCs(_maybeMsg, nextState), ChannelState.Normal _d ->
            { c with State = Negotiating nextState }
        | AcceptedShutdownWhenWeHavePendingHTLCs(_localShutdown, nextState), ChannelState.Normal _normalData ->
            {
                c with
                    State = ChannelState.Normal nextState
            }
        | MutualClosePerformed (_txToPublish, nextState, _), ChannelState.Negotiating _d ->
            { c with State = Closing nextState }
        | WeProposedNewClosingSigned(_msg, nextState), ChannelState.Negotiating _d ->
            { c with State = Negotiating(nextState) }
        // ----- else -----
        | _otherEvent -> c
