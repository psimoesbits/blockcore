﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Asp.Versioning;
using Blockcore.Base;
using Blockcore.Base.Deployments;
using Blockcore.Base.Deployments.Models;
using Blockcore.Configuration;
using Blockcore.Connection;
using Blockcore.Consensus;
using Blockcore.Consensus.BlockInfo;
using Blockcore.Consensus.Chain;
using Blockcore.Consensus.ScriptInfo;
using Blockcore.Consensus.TransactionInfo;
using Blockcore.Controllers;
using Blockcore.Controllers.Models;
using Blockcore.Features.Consensus;
using Blockcore.Features.RPC.Exceptions;
using Blockcore.Features.RPC.ModelBinders;
using Blockcore.Features.RPC.Models;
using Blockcore.Interfaces;
using Blockcore.NBitcoin;
using Blockcore.NBitcoin.DataEncoders;
using Blockcore.Networks;
using Blockcore.Utilities;
using Blockcore.Utilities.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Blockcore.Features.RPC.Controllers
{
    /// <summary>
    /// A <see cref="FeatureController"/> that implements several RPC methods for the full node.
    /// </summary>
    [ApiVersion("1")]
    public class FullNodeController : FeatureController
    {
        /// <summary>Instance logger.</summary>
        private readonly ILogger logger;

        /// <summary>An interface implementation used to retrieve a transaction.</summary>
        private readonly IPooledTransaction pooledTransaction;

        /// <summary>An interface implementation used to retrieve unspent transactions from a pooled source.</summary>
        private readonly IPooledGetUnspentTransaction pooledGetUnspentTransaction;

        /// <summary>An interface implementation used to retrieve unspent transactions.</summary>
        private readonly IGetUnspentTransaction getUnspentTransaction;

        /// <summary>An interface implementation used to retrieve the network difficulty target.</summary>
        private readonly INetworkDifficulty networkDifficulty;

        /// <summary>An interface implementation used to retrieve the total network weight.</summary>
        private readonly INetworkWeight networkWeight;

        /// <summary>An interface implementation for the blockstore.</summary>
        private readonly IBlockStore blockStore;

        /// <summary>A interface implementation for the initial block download state.</summary>
        private readonly IInitialBlockDownloadState ibdState;

        private readonly IStakeChain stakeChain;

        public FullNodeController(
            ILoggerFactory loggerFactory,
            IPooledTransaction pooledTransaction = null,
            IPooledGetUnspentTransaction pooledGetUnspentTransaction = null,
            IGetUnspentTransaction getUnspentTransaction = null,
            INetworkDifficulty networkDifficulty = null,
            IFullNode fullNode = null,
            NodeSettings nodeSettings = null,
            Network network = null,
            ChainIndexer chainIndexer = null,
            IChainState chainState = null,
            IConnectionManager connectionManager = null,
            IConsensusManager consensusManager = null,
            IBlockStore blockStore = null,
            IInitialBlockDownloadState ibdState = null,
            IStakeChain stakeChain = null,
            INetworkWeight networkWeight = null)
            : base(
                  fullNode: fullNode,
                  network: network,
                  nodeSettings: nodeSettings,
                  chainIndexer: chainIndexer,
                  chainState: chainState,
                  connectionManager: connectionManager,
                  consensusManager: consensusManager)
        {
            this.logger = loggerFactory.CreateLogger(this.GetType().FullName);
            this.pooledTransaction = pooledTransaction;
            this.pooledGetUnspentTransaction = pooledGetUnspentTransaction;
            this.getUnspentTransaction = getUnspentTransaction;
            this.networkDifficulty = networkDifficulty;
            this.blockStore = blockStore;
            this.ibdState = ibdState;
            this.stakeChain = stakeChain;
            this.networkWeight = networkWeight;
        }

        /// <summary>
        /// Stops the full node.
        /// </summary>
        [ActionName("stop")]
        [ActionDescription("Stops the full node.")]
        public Task Stop()
        {
            if (this.FullNode != null)
            {
                this.FullNode.NodeLifetime.StopApplication();
                this.FullNode = null;
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Retrieves a transaction given a transaction hash in either simple or verbose form.
        /// </summary>
        /// <param name="txid">The transaction hash.</param>
        /// <param name="verbose">True if verbose model wanted.</param>
        /// <param name="blockHash">The hash of the block in which to look for the transaction.</param>
        /// <returns>A <see cref="TransactionBriefModel"/> or <see cref="TransactionVerboseModel"/> as specified by verbose. <c>null</c> if no transaction matching the hash.</returns>
        /// <exception cref="ArgumentException">Thrown if txid is invalid uint256.</exception>"
        /// <remarks>When called with a blockhash argument, getrawtransaction will return the transaction if the specified block is available and the transaction is found in that block.
        /// When called without a blockhash argument, getrawtransaction will return the transaction if it is in the mempool, or if -txindex is enabled and the transaction is in a block in the blockchain.</remarks>
        [ActionName("getrawtransaction")]
        [ActionDescription("Gets a raw, possibly pooled, transaction from the full node.")]
        public async Task<TransactionModel> GetRawTransactionAsync(string txid, [IntToBool]bool verbose = false, string blockHash = null)
        {
            Guard.NotEmpty(txid, nameof(txid));

            if (!uint256.TryParse(txid, out uint256 trxid))
            {
                throw new ArgumentException(nameof(trxid));
            }

            uint256 hash = null;
            if (!string.IsNullOrEmpty(blockHash) && !uint256.TryParse(blockHash, out hash))
            {
                throw new ArgumentException(nameof(blockHash));
            }

            // Special exception for the genesis block coinbase transaction.
            if (trxid == this.Network.GetGenesis().GetMerkleRoot().Hash)
            {
                throw new RPCServerException(RPCErrorCode.RPC_INVALID_ADDRESS_OR_KEY, "The genesis block coinbase is not considered an ordinary transaction and cannot be retrieved.");
            }

            Transaction trx = null;
            ChainedHeaderBlock chainedHeaderBlock = null;

            if (hash == null)
            {
                // Look for the transaction in the mempool, and if not found, look in the indexed transactions.
                trx = (this.pooledTransaction == null ? null : await this.pooledTransaction.GetTransaction(trxid).ConfigureAwait(false)) ??
                      this.blockStore.GetTransactionById(trxid);

                if (trx == null)
                {
                    throw new RPCServerException(RPCErrorCode.RPC_INVALID_ADDRESS_OR_KEY, "No such mempool transaction. Use -txindex to enable blockchain transaction queries.");
                }
            }
            else
            {
                // Retrieve the block specified by the block hash.
                chainedHeaderBlock = this.ConsensusManager.GetBlockData(hash);

                if (chainedHeaderBlock == null)
                {
                    throw new RPCServerException(RPCErrorCode.RPC_INVALID_ADDRESS_OR_KEY, "Block hash not found.");
                }

                trx = chainedHeaderBlock.Block.Transactions.SingleOrDefault(t => t.GetHash() == trxid);

                if (trx == null)
                {
                    throw new RPCServerException(RPCErrorCode.RPC_INVALID_ADDRESS_OR_KEY, "No such transaction found in the provided block.");
                }
            }

            if (verbose)
            {
                ChainedHeader block = chainedHeaderBlock != null ? chainedHeaderBlock.ChainedHeader : this.GetTransactionBlock(trxid);
                return new TransactionVerboseModel(trx, this.Network, block, this.ChainState?.ConsensusTip);
            }
            else
                return new TransactionBriefModel(trx);
        }

        /// <summary>
        /// Decodes a transaction from its raw hexadecimal format.
        /// </summary>
        /// <param name="hex">The raw transaction hex.</param>
        /// <returns>A <see cref="TransactionVerboseModel"/> or <c>null</c> if the transaction could not be decoded.</returns>
        [ActionName("decoderawtransaction")]
        [ActionDescription("Decodes a serialized transaction hex string into a JSON object describing the transaction.")]
        public TransactionModel DecodeRawTransaction(string hex)
        {
            try
            {
                var transaction = new TransactionVerboseModel(this.FullNode.Network.CreateTransaction(hex), this.Network);

                // Clear hex to not include it into the output. Hex is already known to the client. This will reduce response size.
                transaction.Hex = null;
                return transaction;
            }
            catch (FormatException ex)
            {
                throw new ArgumentException(nameof(hex), ex.Message);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Implements gettextout RPC call.
        /// </summary>
        /// <param name="txid">The transaction id.</param>
        /// <param name="vout">The vout number.</param>
        /// <param name="includeMemPool">Whether to include the mempool.</param>
        /// <returns>A <see cref="GetTxOutModel"/> containing the unspent outputs of the transaction id and vout. <c>null</c> if unspent outputs not found.</returns>
        /// <exception cref="ArgumentException">Thrown if txid is invalid.</exception>"
        [ActionName("gettxout")]
        [ActionDescription("Gets the unspent outputs of a transaction id and vout number.")]
        public async Task<GetTxOutModel> GetTxOutAsync(string txid, uint vout, bool includeMemPool = true)
        {
            uint256 trxid;
            if (!uint256.TryParse(txid, out trxid))
                throw new ArgumentException(nameof(txid));

            UnspentOutput unspentOutputs = null;
            OutPoint outPoint = new OutPoint(trxid, vout);

            if (includeMemPool && this.pooledGetUnspentTransaction != null)
                unspentOutputs = await this.pooledGetUnspentTransaction.GetUnspentTransactionAsync(outPoint).ConfigureAwait(false);

            if (!includeMemPool && this.getUnspentTransaction != null)
                unspentOutputs = await this.getUnspentTransaction.GetUnspentTransactionAsync(outPoint).ConfigureAwait(false);

            if (unspentOutputs != null)
                return new GetTxOutModel(unspentOutputs, this.Network, this.ChainIndexer.Tip);

            return null;
        }

        /// <summary>
        /// Implements the getblockcount RPC call.
        /// </summary>
        /// <returns>The current consensus tip height.</returns>
        [ActionName("getblockcount")]
        [ActionDescription("Gets the current consensus tip height.")]
        public int GetBlockCount()
        {
            return this.ConsensusManager?.Tip.Height ?? -1;
        }

        /// <summary>
        /// Implements the getinfo RPC call.
        /// </summary>
        /// <returns>A <see cref="GetInfoModel"/> with information about the full node.</returns>
        [ActionName("getinfo")]
        [ActionDescription("Gets general information about the full node.")]
        public GetInfoModel GetInfo()
        {
            var model = new GetInfoModel
            {
                Version = this.FullNode?.Version?.ToUint() ?? 0,
                ProtocolVersion = this.Network.Consensus.ConsensusFactory.Protocol.ProtocolVersion,
                Blocks = this.ChainState?.ConsensusTip?.Height ?? 0,
                TimeOffset = this.ConnectionManager?.ConnectedPeers?.GetMedianTimeOffset() ?? 0,
                Connections = this.ConnectionManager?.ConnectedPeers?.Count(),
                Proxy = string.Empty,
                Difficulty = this.GetNetworkDifficulty()?.Difficulty ?? 0,
                Testnet = this.Network.IsTest(),
                RelayFee = this.Settings?.MinRelayTxFeeRate?.FeePerK?.ToUnit(MoneyUnit.BTC) ?? 0,
                Errors = string.Empty,

                //TODO: Wallet related infos: walletversion, balance, keypNetwoololdest, keypoolsize, unlocked_until, paytxfee
                WalletVersion = null,
                Balance = null,
                KeypoolOldest = null,
                KeypoolSize = null,
                UnlockedUntil = null,
                PayTxFee = null
            };

            return model;
        }

        /// <summary>
        /// Implements getblockheader RPC call.
        /// </summary>
        /// <param name="hash">Hash of the block.</param>
        /// <param name="isJsonFormat">Indicates whether to provide data in Json or binary format.</param>
        /// <returns>The block header rpc format.</returns>
        /// <remarks>The binary format is not supported with RPC.</remarks>
        [ActionName("getblockheader")]
        [ActionDescription("Gets the block header of the block identified by the hash.")]
        public object GetBlockHeader(string hash, bool isJsonFormat = true)
        {
            Guard.NotNull(hash, nameof(hash));

            this.logger.LogDebug("RPC GetBlockHeader {0}", hash);

            if (this.ChainIndexer == null)
                return null;

            BlockHeader blockHeader = this.ChainIndexer.GetHeader(uint256.Parse(hash))?.Header;

            if (blockHeader == null)
                return null;

            if (isJsonFormat)
                return new BlockHeaderModel(blockHeader);

            return new HexModel(blockHeader.ToHex(this.Network.Consensus.ConsensusFactory));
        }

        /// <summary>
        /// Returns information about a bitcoin address and it's validity.
        /// </summary>
        /// <param name="address">The bech32 or base58 <see cref="BitcoinAddress"/> to validate.</param>
        /// <returns><see cref="ValidatedAddress"/> instance containing information about the bitcoin address and it's validity.</returns>
        /// <exception cref="ArgumentNullException">Thrown if address provided is null or empty.</exception>
        [ActionName("validateaddress")]
        [ActionDescription("Returns information about a bech32 or base58 bitcoin address")]
        public ValidatedAddress ValidateAddress(string address)
        {
            Guard.NotEmpty(address, nameof(address));

            var result = new ValidatedAddress
            {
                IsValid = false,
                Address = address,
            };

            try
            {
                // P2WPKH
                if (BitcoinWitPubKeyAddress.IsValid(address, this.Network, out Exception _))
                {
                    result.IsValid = true;
                }
                // P2WSH
                else if (BitcoinWitScriptAddress.IsValid(address, this.Network, out Exception _))
                {
                    result.IsValid = true;
                }
                // P2PKH
                else if (BitcoinPubKeyAddress.IsValid(address, this.Network))
                {
                    result.IsValid = true;
                }
                // P2SH
                else if (BitcoinScriptAddress.IsValid(address, this.Network))
                {
                    result.IsValid = true;
                    result.IsScript = true;
                }
            }
            catch (NotImplementedException)
            {
                result.IsValid = false;
            }

            if (result.IsValid)
            {
                var scriptPubKey = BitcoinAddress.Create(address, this.Network).ScriptPubKey;
                result.ScriptPubKey = scriptPubKey.ToHex();
                result.IsWitness = scriptPubKey.IsScriptType(ScriptType.Witness);
            }

            return result;
        }

        /// <summary>
        /// RPC method for returning a block.
        /// <para>
        /// Supports Json format by default, and optionally raw (hex) format by supplying <c>0</c> to <see cref="verbosity"/>.
        /// </para>
        /// </summary>
        /// <param name="blockHash">Hash of block to find.</param>
        /// <param name="verbosity">Defaults to 1. 0 for hex encoded data, 1 for a json object, and 2 for json object with transaction data.</param>
        /// <returns>The block according to format specified in <see cref="verbosity"/></returns>
        [ActionName("getblock")]
        [ActionDescription("Returns the block in hex, given a block hash.")]
        public object GetBlock(string blockHash, int verbosity = 1)
        {
            uint256 blockId = uint256.Parse(blockHash);

            // Does the block exist.
            ChainedHeader chainedHeader = this.ChainIndexer.GetHeader(blockId);

            if (chainedHeader == null)
                return null;

            Block block = chainedHeader.Block ?? this.blockStore?.GetBlock(blockId);

            // In rare occasions a block that is found in the
            // indexer may not have been pushed to the store yet.
            if (block == null)
                return null;

            if (verbosity == 0)
                return new HexModel(block.ToHex(this.Network.Consensus.ConsensusFactory));

            var blockModel = new BlockModel(block, chainedHeader, this.ChainIndexer.Tip, this.Network, verbosity);

            if (this.Network.Consensus.IsProofOfStake)
            {
                var posBlock = block as PosBlock;

                blockModel.PosBlockSignature = posBlock.BlockSignature.ToHex(this.Network.Consensus.ConsensusFactory);
                blockModel.PosBlockTrust = new Target(chainedHeader.GetBlockTarget()).ToUInt256().ToString();
                blockModel.PosChainTrust = chainedHeader.ChainWork.ToString(); // this should be similar to ChainWork

                if (this.stakeChain != null)
                {
                    BlockStake blockStake = this.stakeChain.Get(blockId);

                    blockModel.PosModifierv2 = blockStake?.StakeModifierV2.ToString();
                    blockModel.PosFlags = blockStake?.Flags == BlockFlag.BLOCK_PROOF_OF_STAKE ? "proof-of-stake" : "proof-of-work";
                    blockModel.PosHashProof = blockStake?.HashProof.ToString();
                }
            }

            return blockModel;
        }

        [ActionName("getnetworkinfo")]
        [ActionDescription("Returns an object containing various state info regarding P2P networking.")]
        public NetworkInfoModel GetNetworkInfo()
        {
            var networkInfoModel = new NetworkInfoModel
            {
                Version = this.FullNode?.Version?.ToUint() ?? 0,
                SubVersion = this.Settings?.Agent,
                ProtocolVersion = this.Network.Consensus.ConsensusFactory.Protocol.ProtocolVersion,
                IsLocalRelay = this.ConnectionManager?.Parameters?.IsRelay ?? false,
                TimeOffset = this.ConnectionManager?.ConnectedPeers?.GetMedianTimeOffset() ?? 0,
                Connections = this.ConnectionManager?.ConnectedPeers?.Count(),
                IsNetworkActive = true,
                RelayFee = this.Settings?.MinRelayTxFeeRate?.FeePerK?.ToUnit(MoneyUnit.BTC) ?? 0,
                IncrementalFee = this.Settings?.MinRelayTxFeeRate?.FeePerK?.ToUnit(MoneyUnit.BTC) ?? 0 // set to same as min relay fee
            };

            var services = this.ConnectionManager?.Parameters?.Services;
            if (services != null)
            {
                networkInfoModel.LocalServices = Encoders.Hex.EncodeData(BitConverter.GetBytes((ulong)services));
            }

            return networkInfoModel;
        }

        [ActionName("getblockchaininfo")]
        [ActionDescription("Returns an object containing various state info regarding blockchain processing.")]
        public BlockchainInfoModel GetBlockchainInfo()
        {
            var blockchainInfo = new BlockchainInfoModel
            {
                Chain = this.Network?.Name,
                Blocks = (uint)(this.ChainState?.ConsensusTip?.Height ?? 0),
                Headers = (uint)(this.ChainIndexer?.Height ?? 0),
                BestBlockHash = this.ChainState?.ConsensusTip?.HashBlock,
                Difficulty = this.GetNetworkDifficulty()?.Difficulty ?? 0.0,
                NetworkWeight = (long)this.GetPosNetworkWeight(),
                MedianTime = this.ChainState?.ConsensusTip?.GetMedianTimePast().ToUnixTimeSeconds() ?? 0,
                VerificationProgress = 0.0,
                IsInitialBlockDownload = this.ibdState?.IsInitialBlockDownload() ?? true,
                Chainwork = this.ChainState?.ConsensusTip?.ChainWork,
                IsPruned = false
            };

            if (blockchainInfo.Headers > 0)
            {
                blockchainInfo.VerificationProgress = (double)blockchainInfo.Blocks / blockchainInfo.Headers;
            }

            // softfork deployments
            blockchainInfo.SoftForks = new List<SoftForks>();

            foreach (var consensusBuriedDeployment in Enum.GetValues(typeof(BuriedDeployments)))
            {
                bool active = this.ChainIndexer.Height >= this.Network.Consensus.BuriedDeployments[(BuriedDeployments)consensusBuriedDeployment];
                blockchainInfo.SoftForks.Add(new SoftForks
                {
                    Id = consensusBuriedDeployment.ToString().ToLower(),
                    Version = (int)consensusBuriedDeployment + 2, // hack to get the deployment number similar to bitcoin core without changing the enums
                    Status = new SoftForksStatus { Status = active }
                });
            }

            // softforkbip9 deployments
            blockchainInfo.SoftForksBip9 = new Dictionary<string, SoftForksBip9>();

            ConsensusRuleEngine ruleEngine = (ConsensusRuleEngine)this.ConsensusManager.ConsensusRules;
            ThresholdState[] thresholdStates = ruleEngine.NodeDeployments.BIP9.GetStates(this.ChainIndexer.Tip.Previous);
            List<ThresholdStateModel> metrics = ruleEngine.NodeDeployments.BIP9.GetThresholdStateMetrics(this.ChainIndexer.Tip.Previous, thresholdStates);

            foreach (ThresholdStateModel metric in metrics.Where(m => !m.DeploymentName.ToLower().Contains("test"))) // to remove the test dummy
            {
                // TODO: Deployment timeout may not be implemented yet

                // Deployments with timeout value of 0 are hidden.
                // A timeout value of 0 guarantees a softfork will never be activated.
                // This is used when softfork codes are merged without specifying the deployment schedule.
                if (metric.TimeTimeOut?.Ticks > 0)
                    blockchainInfo.SoftForksBip9.Add(metric.DeploymentName, this.CreateSoftForksBip9(metric, thresholdStates[metric.DeploymentIndex]));
            }

            // TODO: Implement blockchainInfo.warnings
            return blockchainInfo;
        }

        private SoftForksBip9 CreateSoftForksBip9(ThresholdStateModel metric, ThresholdState state)
        {
            var softForksBip9 = new SoftForksBip9()
            {
                Status = metric.ThresholdState.ToLower(),
                Bit = this.Network.Consensus.BIP9Deployments[metric.DeploymentIndex].Bit,
                StartTime = metric.TimeStart?.ToUnixTimestamp() ?? 0,
                Timeout = metric.TimeTimeOut?.ToUnixTimestamp() ?? 0,
                Since = metric.SinceHeight
            };

            if (state == ThresholdState.Started)
            {
                softForksBip9.Statistics = new SoftForksBip9Statistics();

                softForksBip9.Statistics.Period = metric.ConfirmationPeriod;
                softForksBip9.Statistics.Threshold = (int)metric.Threshold;
                softForksBip9.Statistics.Count = metric.Blocks;
                softForksBip9.Statistics.Elapsed = metric.Height - metric.PeriodStartHeight;
                softForksBip9.Statistics.Possible = (softForksBip9.Statistics.Period - softForksBip9.Statistics.Threshold) >= (softForksBip9.Statistics.Elapsed - softForksBip9.Statistics.Count);
            }

            return softForksBip9;
        }

        private ChainedHeader GetTransactionBlock(uint256 trxid)
        {
            ChainedHeader block = null;

            uint256 blockid = this.blockStore?.GetBlockIdByTransactionId(trxid);
            if (blockid != null)
                block = this.ChainIndexer?.GetHeader(blockid);

            return block;
        }

        private Target GetNetworkDifficulty()
        {
            return this.networkDifficulty?.GetNetworkDifficulty();
        }

        private double GetPosNetworkWeight()
        {
            return this.networkWeight?.GetPosNetworkWeight() ?? 0;
        }
    }
}