namespace Own.Blockchain.Public.Faucet

open Own.Blockchain.Public.Core.DomainTypes
open Own.Blockchain.Public.Crypto

module Composition =

    let getAddressNonce = NodeClient.getAddressNonce Config.NodeApiUrl

    let submitTx = NodeClient.submitTx Config.NodeApiUrl

    let claimChx = Workflows.claimChx Hashing.isValidBlockchainAddress (ChxAmount Config.MaxClaimableChxAmount)

    let claimAsset = Workflows.claimAsset Hashing.isValidHash (AssetAmount Config.MaxClaimableAssetAmount)

    let getNetworkId () =
        Hashing.networkId Config.NetworkCode

    let distributeChx () =
        Workflows.distributeChx
            getAddressNonce
            submitTx
            Hashing.hash
            (Signing.signHash getNetworkId)
            (PrivateKey Config.FaucetSupplyHolderPrivateKey)
            (BlockchainAddress Config.FaucetSupplyHolderAddress)
            (ChxAmount Config.ActionFee)
            (int Config.DistributionBatchSize)
            (ChxAmount Config.MaxClaimableChxAmount)

    let distributeAsset () =
        Workflows.distributeAsset
            getAddressNonce
            submitTx
            Hashing.hash
            (Signing.signHash getNetworkId)
            (PrivateKey Config.FaucetSupplyHolderPrivateKey)
            (BlockchainAddress Config.FaucetSupplyHolderAddress)
            (ChxAmount Config.ActionFee)
            (int Config.DistributionBatchSize)
            (AssetAmount Config.MaxClaimableAssetAmount)
            (AssetHash Config.FaucetSupplyAssetHash)
            (AccountHash Config.FaucetSupplyHolderAccountHash)
