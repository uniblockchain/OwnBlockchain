namespace Chainium.Blockchain.Public.Core

open System
open Chainium.Common
open Chainium.Blockchain.Public.Core
open Chainium.Blockchain.Public.Core.DomainTypes
open System.Collections.Concurrent

module Processing =

    type ProcessingState
        (
        getChxBalanceStateFromStorage : ChainiumAddress -> ChxBalanceState,
        getHoldingStateFromStorage : AccountHash * EquityID -> HoldingState,
        txResults : ConcurrentDictionary<TxHash, TxProcessedStatus>,
        chxBalances : ConcurrentDictionary<ChainiumAddress, ChxBalanceState>,
        holdings : ConcurrentDictionary<AccountHash * EquityID, HoldingState>
        ) =

        new
            (
            getChxBalanceStateFromStorage : ChainiumAddress -> ChxBalanceState,
            getHoldingStateFromStorage : AccountHash * EquityID -> HoldingState
            ) =
            ProcessingState(
                getChxBalanceStateFromStorage,
                getHoldingStateFromStorage,
                ConcurrentDictionary<TxHash, TxProcessedStatus>(),
                ConcurrentDictionary<ChainiumAddress, ChxBalanceState>(),
                ConcurrentDictionary<AccountHash * EquityID, HoldingState>()
            )

        member __.Clone () =
            ProcessingState(
                getChxBalanceStateFromStorage,
                getHoldingStateFromStorage,
                ConcurrentDictionary(txResults),
                ConcurrentDictionary(chxBalances),
                ConcurrentDictionary(holdings)
            )

        member __.SetTxStatus (txHash : TxHash, txStatus : TxProcessedStatus) =
            txResults.AddOrUpdate(txHash, txStatus, fun _ _ -> txStatus) |> ignore

        member __.GetChxBalance (address : ChainiumAddress) =
            chxBalances.GetOrAdd(address, getChxBalanceStateFromStorage)

        member __.GetHolding (accountHash : AccountHash, equityID : EquityID) =
            holdings.GetOrAdd((accountHash, equityID), getHoldingStateFromStorage)

        member __.SetChxBalance (address : ChainiumAddress, state : ChxBalanceState) =
            chxBalances.AddOrUpdate(address, state, fun _ _ -> state) |> ignore

        member __.SetHolding (accountHash : AccountHash, equityID : EquityID, state : HoldingState) =
            holdings.AddOrUpdate((accountHash, equityID), state, fun _ _ -> state) |> ignore

        member __.ToProcessingOutput () : ProcessingOutput =
            {
                TxResults = txResults |> Map.ofDict
                ChxBalances = chxBalances |> Map.ofDict
                Holdings = holdings |> Map.ofDict
            }

    ////////////////////////////////////////////////////////////////////////////////////////////////////
    // Action Processing
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    let processChxTransferTxAction
        (state : ProcessingState)
        (senderAddress : ChainiumAddress)
        (action : ChxTransferTxAction)
        : Result<ProcessingState, AppErrors>
        =

        let fromState = state.GetChxBalance(senderAddress)
        let toState = state.GetChxBalance(action.RecipientAddress)

        let (ChxAmount fromBalance) = fromState.Amount
        let (ChxAmount toBalance) = toState.Amount
        let (ChxAmount amountToTransfer) = action.Amount

        if fromBalance < amountToTransfer then
            Error [AppError "CHX balance too low."]
        else
            let fromState = { fromState with Amount = ChxAmount (fromBalance - amountToTransfer) }
            let toState = { toState with Amount = ChxAmount (toBalance + amountToTransfer) }
            state.SetChxBalance(senderAddress, fromState)
            state.SetChxBalance(action.RecipientAddress, toState)
            Ok state

    ////////////////////////////////////////////////////////////////////////////////////////////////////
    // Tx Processing
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    let excludeUnprocessableTxs
        (getChxBalanceState : ChainiumAddress -> ChxBalanceState)
        (txSet : PendingTxInfo list)
        =

        let excludeUnprocessableTxsForAddress senderAddress (txSet : PendingTxInfo list) =
            let stateNonce = (getChxBalanceState senderAddress).Nonce

            let (destinedToFailDueToLowNonce, rest) =
                txSet
                |> List.partition(fun tx -> tx.Nonce <= stateNonce)

            rest
            |> List.sortBy (fun tx -> tx.Nonce)
            |> List.mapi (fun i tx ->
                let nonceGap =
                    (stateNonce, tx.Nonce)
                    |> fun (Nonce stateNonce, Nonce txNonce) ->
                        let expectedNonce = stateNonce + (int64 (i + 1))
                        txNonce - expectedNonce
                (tx, nonceGap)
            )
            |> List.takeWhile (fun (_, nonceGap) -> nonceGap = 0L)
            |> List.map fst
            |> List.append destinedToFailDueToLowNonce

        txSet
        |> List.groupBy (fun tx -> tx.Sender)
        |> List.collect (fun (senderAddress, txs) -> excludeUnprocessableTxsForAddress senderAddress txs)
        |> List.sortBy (fun tx -> tx.AppearanceOrder)

    let getTxSetForNewBlock getPendingTxs getChxBalanceState maxTxCountPerBlock : PendingTxInfo list =
        let rec getTxSet txHashesToSkip (txSet : PendingTxInfo list) =
            let txCountToFetch = maxTxCountPerBlock - txSet.Length
            let fetchedTxs =
                getPendingTxs txHashesToSkip txCountToFetch
                |> List.map Mapping.pendingTxInfoFromDto
            let txSet = excludeUnprocessableTxs getChxBalanceState (txSet @ fetchedTxs)
            if txSet.Length = maxTxCountPerBlock || fetchedTxs.Length = 0 then
                txSet
            else
                let txHashesToSkip =
                    fetchedTxs
                    |> List.map (fun t -> t.TxHash)
                    |> List.append txHashesToSkip

                getTxSet txHashesToSkip txSet

        getTxSet [] []

    let orderTxSet (txSet : PendingTxInfo list) : TxHash list =
        let rec orderSet orderedSet unorderedSet =
            match unorderedSet with
            | [] -> orderedSet
            | head :: tail ->
                let (precedingTxsForSameSender, rest) =
                    tail
                    |> List.partition (fun tx ->
                        tx.Sender = head.Sender
                        && (
                            tx.Nonce < head.Nonce
                            || (tx.Nonce = head.Nonce && tx.Fee > head.Fee)
                        )
                    )
                let precedingTxsForSameSender =
                    precedingTxsForSameSender
                    |> List.sortBy (fun tx -> tx.Nonce, tx.Fee |> fun (ChxAmount a) -> -a)
                let orderedSet =
                    orderedSet
                    @ precedingTxsForSameSender
                    @ [head]
                orderSet orderedSet rest

        txSet
        |> List.sortBy (fun tx -> tx.AppearanceOrder)
        |> orderSet []
        |> List.map (fun tx -> tx.TxHash)

    let getTxBody getTx verifySignature txHash =
        result {
            let! txEnvelopeDto = getTx txHash
            let txEnvelope = Mapping.txEnvelopeFromDto txEnvelopeDto

            let! sender = Validation.verifyTxSignature verifySignature txEnvelope

            let! tx =
                txEnvelope.RawTx
                |> Serialization.deserializeTx
                >>= (Validation.validateTx sender txHash)

            return tx
        }

    let calculateTotalFee (tx : Tx) =
        let (ChxAmount fee) = tx.Fee
        fee * (decimal tx.Actions.Length) |> ChxAmount

    let processValidatorReward (state : ProcessingState) (tx : Tx) validator =
        {
            ChxTransferTxAction.RecipientAddress = validator
            Amount = calculateTotalFee tx
        }
        |> processChxTransferTxAction state tx.Sender

    let processTxAction (state : ProcessingState) (senderAddress : ChainiumAddress) = function
        | ChxTransfer action -> processChxTransferTxAction state senderAddress action
        | EquityTransfer action -> failwith "TODO: EquityTransfer"

    let processTxActions (state : ProcessingState) (senderAddress : ChainiumAddress) (actions : TxAction list) =
        let processAction result action =
            result
            >>= fun state -> processTxAction state senderAddress action

        actions
        |> List.fold processAction (Ok state)

    let processTxSet
        getTx
        verifySignature
        (getChxBalanceStateFromStorage : ChainiumAddress -> ChxBalanceState)
        (getHoldingStateFromStorage : AccountHash * EquityID -> HoldingState)
        (validator : ChainiumAddress)
        (txSet : TxHash list)
        =

        let processTx (oldState : ProcessingState) (txHash : TxHash) =
            let newState = oldState.Clone()

            let processingResult =
                result {
                    let! tx = getTxBody getTx verifySignature txHash

                    let! state =
                        processValidatorReward newState tx validator
                        >>= fun state -> processTxActions state tx.Sender tx.Actions

                    return state
                }

            match processingResult with
            | Error _ ->
                // TODO: Persist error message as well
                oldState.SetTxStatus(txHash, Failure)
                oldState
            | Ok _ ->
                newState.SetTxStatus(txHash, Success)
                newState

        let initialState = ProcessingState (getChxBalanceStateFromStorage, getHoldingStateFromStorage)

        let state =
            txSet
            |> List.fold processTx initialState

        state.ToProcessingOutput()
