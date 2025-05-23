﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Blockcore.AsyncWork;
using Blockcore.Base;
using Blockcore.BlockPulling;
using Blockcore.Configuration;
using Blockcore.Configuration.Settings;
using Blockcore.Connection;
using Blockcore.Consensus;
using Blockcore.Consensus.BlockInfo;
using Blockcore.Consensus.Chain;
using Blockcore.Consensus.Checkpoints;
using Blockcore.Consensus.Rules;
using Blockcore.Consensus.ScriptInfo;
using Blockcore.Consensus.TransactionInfo;
using Blockcore.Consensus.Validators;
using Blockcore.Features.Consensus.CoinViews;
using Blockcore.Features.Consensus.Rules;
using Blockcore.Features.Consensus.Rules.CommonRules;
using Blockcore.Features.Consensus.Rules.UtxosetRules;
using Blockcore.Interfaces;
using Blockcore.NBitcoin;
using Blockcore.NBitcoin.Crypto;
using Blockcore.Tests.Common;
using Blockcore.Utilities;
using Moq;
using Xunit;

namespace Blockcore.Features.Consensus.Tests.Rules.CommonRules
{
    /// <summary>
    /// Test cases related to the <see cref="CheckPosUtxosetRule"/>.
    /// </summary>
    public class PosCoinViewRuleTests : TestPosConsensusRulesUnitTestBase
    {
        /// <summary>
        /// Creates the consensus manager used by <see cref="PosCoinViewRuleFailsAsync"/>.
        /// </summary>
        /// <param name="unspentOutputs">The dictionary used to mock up the <see cref="ICoinView"/>.</param>
        /// <returns>The constructed consensus manager.</returns>
        private async Task<ConsensusManager> CreateConsensusManagerAsync(Dictionary<OutPoint, UnspentOutput> unspentOutputs)
        {
            this.consensusSettings = new ConsensusSettings(NodeSettings.Default(this.network));
            var initialBlockDownloadState = new InitialBlockDownloadState(this.chainState.Object, this.network, this.consensusSettings, new Checkpoints(), this.loggerFactory.Object, DateTimeProvider.Default);
            var signals = new Signals.Signals(this.loggerFactory.Object, null);
            var asyncProvider = new AsyncProvider(this.loggerFactory.Object, signals, new Mock<INodeLifetime>().Object);

            var consensusRulesContainer = new ConsensusRulesContainer();
            foreach (var ruleType in this.network.Consensus.ConsensusRules.FullValidationRules)
                consensusRulesContainer.FullValidationRules.Add(Activator.CreateInstance(ruleType) as FullValidationConsensusRule);

            // Register POS consensus rules.
            // new FullNodeBuilderConsensusExtension.PosConsensusRulesRegistration().RegisterRules(this.network.Consensus);
            var consensusRuleEngine = new PosConsensusRuleEngine(this.network, this.loggerFactory.Object, DateTimeProvider.Default,
                this.ChainIndexer, this.nodeDeployments, this.consensusSettings, this.checkpoints.Object, this.coinView.Object, this.stakeChain.Object,
                this.stakeValidator.Object, this.chainState.Object, new InvalidBlockHashStore(this.dateTimeProvider.Object), new Mock<INodeStats>().Object, this.rewindDataIndexStore.Object, this.asyncProvider, consensusRulesContainer)
                .SetupRulesEngineParent();

            var headerValidator = new HeaderValidator(consensusRuleEngine, this.loggerFactory.Object);
            var integrityValidator = new IntegrityValidator(consensusRuleEngine, this.loggerFactory.Object);
            var partialValidator = new PartialValidator(asyncProvider, consensusRuleEngine, this.loggerFactory.Object);
            var fullValidator = new FullValidator(consensusRuleEngine, this.loggerFactory.Object);

            // Create the chained header tree.
            var chainedHeaderTree = new ChainedHeaderTree(this.network, this.loggerFactory.Object, headerValidator, this.checkpoints.Object,
                this.chainState.Object, new Mock<IFinalizedBlockInfoRepository>().Object, this.consensusSettings, new InvalidBlockHashStore(new DateTimeProvider()));

            // Create consensus manager.
            var consensus = new ConsensusManager(chainedHeaderTree, this.network, this.loggerFactory.Object, this.chainState.Object, integrityValidator,
                partialValidator, fullValidator, consensusRuleEngine, new Mock<IFinalizedBlockInfoRepository>().Object, signals,
                new Mock<IPeerBanning>().Object, initialBlockDownloadState, this.ChainIndexer, new Mock<IBlockPuller>().Object, new Mock<IBlockStore>().Object,
                new Mock<IConnectionManager>().Object, new Mock<INodeStats>().Object, new Mock<INodeLifetime>().Object, this.consensusSettings, this.dateTimeProvider.Object);

            // Mock the coinviews "FetchCoinsAsync" method. We will use the "unspentOutputs" dictionary to track spendable outputs.
            this.coinView.Setup(d => d.FetchCoins(It.IsAny<IReadOnlyCollection<OutPoint>>()))
                .Returns((IReadOnlyCollection<OutPoint> txIds) =>
                {
                    var result = new FetchCoinsResponse();

                    foreach(var txId in txIds)
                    {
                        unspentOutputs.TryGetValue(txId, out UnspentOutput unspent);
                        result.UnspentOutputs.Add(txId, unspent);
                    }

                    return result;
                });

            // Mock the coinviews "GetTipHashAsync" method.
            this.coinView.Setup(d => d.GetTipHash()).Returns(() =>
            {
                return new HashHeightPair(this.ChainIndexer.Tip);
            });

            // Since we are mocking the stake validator ensure that GetNextTargetRequired returns something sensible. Otherwise we get the "bad-diffbits" error.
            this.stakeValidator.Setup(s => s.GetNextTargetRequired(It.IsAny<IStakeChain>(), It.IsAny<ChainedHeader>(), It.IsAny<IConsensus>(), It.IsAny<bool>()))
                .Returns(this.network.Consensus.PowLimit);

            // Skip validation of signature in the proven header
            this.stakeValidator.Setup(s => s.VerifySignature(It.IsAny<UnspentOutput>(), It.IsAny<Transaction>(), 0, It.IsAny<ScriptVerify>()))
                .Returns(true);

            // Skip validation of stake kernel
            this.stakeValidator.Setup(s => s.CheckStakeKernelHash(It.IsAny<PosRuleContext>(), It.IsAny<uint>(), It.IsAny<uint256>(), It.IsAny<UnspentOutput>(), It.IsAny<OutPoint>(), It.IsAny<uint>()))
                .Returns(true);

            // Since we are mocking the stakechain ensure that the Get returns a BlockStake. Otherwise this results in "previous stake is not found".
            this.stakeChain.Setup(d => d.Get(It.IsAny<uint256>())).Returns(new BlockStake()
            {
                Flags = BlockFlag.BLOCK_PROOF_OF_STAKE,
                StakeModifierV2 = 0,
                StakeTime = (this.ChainIndexer.Tip.Header.Time + 60) & ~this.network.Consensus.ProofOfStakeTimestampMask
            });

            // Since we are mocking the chainState ensure that the BlockStoreTip returns a usable value.
            this.chainState.Setup(d => d.BlockStoreTip).Returns(this.ChainIndexer.Tip);

            // Since we are mocking the chainState ensure that the ConsensusTip returns a usable value.
            this.chainState.Setup(d => d.ConsensusTip).Returns(this.ChainIndexer.Tip);

            // Initialize the consensus manager.
            await consensus.InitializeAsync(this.ChainIndexer.Tip);

            return consensus;
        }

        /// <summary>
        /// Tests whether an error is raised when a miner attempts to stake an output which he can't spend with his private key.
        /// </summary>
        /// <remarks>
        /// <para>Create a "previous transaction" with 2 outputs. The first output is sent to miner 2 and the second output is sent to miner 1.
        /// Now miner 2 creates a proof of stake block with coinstake transaction which will have two inputs corresponding to both the
        /// outputs of the previous transaction. The coinstake transaction will be just two outputs, first is the coinstake marker and
        /// the second is normal pay to public key that belongs to miner 2 with value that equals to the sum of the inputs.
        /// The testable outcome is whether the consensus engine accepts such a block. Obviously, the test should fail if the block is accepted.
        /// </para><para>
        /// We use <see cref="ConsensusManager.BlockMinedAsync(Block)"/> to run partial validation and full validation rules and expect that
        /// the rules engine will reject the block with the specific error of <see cref="ConsensusErrors.BadTransactionScriptError"/>,
        /// which confirms that there was no valid signature on the second input - which corresponds to the output sent to miner 1.
        /// </para></remarks>
        [Fact]
        public async Task PosCoinViewRuleFailsAsync()
        {
            var unspentOutputs = new Dictionary<OutPoint, UnspentOutput>();

            ConsensusManager consensusManager = await this.CreateConsensusManagerAsync(unspentOutputs);

            // The keys used by miner 1 and miner 2.
            var minerKey1 = new Key();
            var minerKey2 = new Key();

            // The scriptPubKeys (P2PK) of the miners.
            Script scriptPubKey1 = minerKey1.PubKey.ScriptPubKey;
            Script scriptPubKey2 = minerKey2.PubKey.ScriptPubKey;

            // Create the block that we want to validate.
            Block block = this.network.Consensus.ConsensusFactory.CreateBlock();

            // Add dummy first transaction.
            Transaction transaction = this.network.CreateTransaction();
            transaction.Inputs.Add(TxIn.CreateCoinbase(this.ChainIndexer.Tip.Height + 1));
            transaction.Outputs.Add(new TxOut(Money.Zero, (IDestination)null));
            Assert.True(transaction.IsCoinBase);

            // Add first transaction to block.
            block.Transactions.Add(transaction);

            // Create a previous transaction with scriptPubKey outputs.
            Transaction prevTransaction = this.network.CreateTransaction();

            uint blockTime = (this.ChainIndexer.Tip.Header.Time + 60) & ~this.network.Consensus.ProofOfStakeTimestampMask;

            // To avoid violating the transaction timestamp consensus rule
            // we need to ensure that the transaction used for the coinstake's
            // input occurs well before the block time (as the coinstake time
            // is set to the block time)
            if (prevTransaction is IPosTransactionWithTime posTrx)
                posTrx.Time = blockTime - 100;

            // Coins sent to miner 2.
            prevTransaction.Outputs.Add(new TxOut(Money.COIN * 5_000_000, scriptPubKey2));
            // Coins sent to miner 1.
            prevTransaction.Outputs.Add(new TxOut(Money.COIN * 10_000_000, scriptPubKey1));

            // Record the spendable outputs.
            unspentOutputs.Add(new OutPoint(prevTransaction, 0), new UnspentOutput(new OutPoint(prevTransaction, 0), new Coins(1, prevTransaction.Outputs[0], prevTransaction.IsCoinBase)));
            unspentOutputs.Add(new OutPoint(prevTransaction, 1), new UnspentOutput(new OutPoint(prevTransaction, 1), new Coins(1, prevTransaction.Outputs[1], prevTransaction.IsCoinBase)));

            // Create coin stake transaction.
            Transaction coinstakeTransaction = this.network.CreateTransaction();

            coinstakeTransaction.Inputs.Add(new TxIn(new OutPoint(prevTransaction, 0)));
            coinstakeTransaction.Inputs.Add(new TxIn(new OutPoint(prevTransaction, 1)));

            // Coinstake marker.
            coinstakeTransaction.Outputs.Add(new TxOut(Money.Zero, (IDestination)null));
            // Normal pay to public key that belongs to the second miner with value that
            // equals to the sum of the inputs.
            coinstakeTransaction.Outputs.Add(new TxOut(Money.COIN * 15_000_000, scriptPubKey2));

            // The second miner signs the first transaction input which requires minerKey2.
            // Miner 2 will not have minerKey1 so we leave the second ScriptSig empty/invalid.
            new TransactionBuilder(this.network)
                .AddKeys(minerKey2)
                .AddCoins(new Coin(new OutPoint(prevTransaction, 0), prevTransaction.Outputs[0]))
                .SignTransactionInPlace(coinstakeTransaction);

            Assert.True(coinstakeTransaction.IsCoinStake);

            // Add second transaction to block.
            block.Transactions.Add(coinstakeTransaction);

            // Finalize the block and add it to the chain.
            block.Header.HashPrevBlock = this.ChainIndexer.Tip.HashBlock;
            block.Header.Time = blockTime;
            block.Header.Bits = block.Header.GetWorkRequired(this.network, this.ChainIndexer.Tip);
            block.SetPrivatePropertyValue("BlockSize", 1L);
            block.UpdateMerkleRoot();
            Assert.True(BlockStake.IsProofOfStake(block));
            // Add a signature to the block.
            ECDSASignature signature = minerKey2.Sign(block.GetHash());
            (block as PosBlock).BlockSignature = new BlockSignature { Signature = signature.ToDER() };

            // Execute the rule and check the outcome against what is expected.
            ConsensusException error = await Assert.ThrowsAsync<ConsensusException>(async () => await consensusManager.BlockMinedAsync(block));
            Assert.Equal(ConsensusErrors.BadTransactionScriptError.Message, error.Message);
        }
    }
}