﻿using System;
using System.Linq;
using Blockcore.Consensus;
using Blockcore.Consensus.BlockInfo;
using Blockcore.Consensus.Chain;
using Blockcore.Consensus.ScriptInfo;
using Blockcore.Consensus.TransactionInfo;
using Blockcore.Features.Consensus.CoinViews;
using Blockcore.Features.Consensus.Rules.ProvenHeaderRules;
using Blockcore.NBitcoin;
using Blockcore.NBitcoin.BIP39;
using Blockcore.NBitcoin.Crypto;
using Blockcore.Tests.Common;
using Blockcore.Utilities;
using FluentAssertions;
using Moq;
using Xunit;
using uint256 = Blockcore.NBitcoin.uint256;

namespace Blockcore.Features.Consensus.Tests.Rules.ProvenHeaderRules
{
    public class ProvenBlockHeaderCoinstakeRuleTest : TestPosConsensusRulesUnitTestBase
    {
        private readonly PosConsensusOptions options;
        private int provenHeadersActivationHeight;

        public ProvenBlockHeaderCoinstakeRuleTest()
        {
            this.options = (PosConsensusOptions)this.network.Consensus.Options;
            this.provenHeadersActivationHeight = this.network.Checkpoints.Keys.Last();
        }

        [Fact]
        public void RunRule_ProvenHeadersNotActive_RuleIsSkipped()
        {
            // Setup proven header.
            PosBlock posBlock = new PosBlockBuilder(this.network).Build();
            ProvenBlockHeader provenBlockHeader = new ProvenBlockHeaderBuilder(posBlock, this.network).Build();

            // Setup chained header and move it to the height below proven header activation height.
            this.ruleContext.ValidationContext.ChainedHeaderToValidate = new ChainedHeader(provenBlockHeader.PosBlockHeader, provenBlockHeader.GetHash(), null);
            this.checkpoints.Setup(c => c.LastCheckpointHeight).Returns(100);

            // When we run the validation rule, we should not hit any exceptions as rule will be skipped.
            Action ruleValidation = () => this.consensusRules.RegisterRule<ProvenHeaderCoinstakeRule>().Run(this.ruleContext);
            ruleValidation.Should().NotThrow();
        }

        [Fact]
        public void RunRule_ContextChainedHeaderIsNull_ArgumentNullExceptionIsThrown()
        {
            // Setup null chained header.
            this.ruleContext.ValidationContext.ChainedHeaderToValidate = null;

            // When we run the validation rule, we should hit null argument exception for chained header.
            Action ruleValidation = () => this.consensusRules.RegisterRule<ProvenHeaderCoinstakeRule>().Run(this.ruleContext);
            ruleValidation.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void RunRule_ProvenHeadersActive_And_CoinstakeIsNull_EmptyCoinstakeErrorIsThrown()
        {
            // Setup proven header.
            PosBlock posBlock = new PosBlockBuilder(this.network).Build();
            ProvenBlockHeader provenBlockHeader = new ProvenBlockHeaderBuilder(posBlock, this.network).Build();

            // Setup chained header and move it to the height higher than proven header activation height.
            this.ruleContext.ValidationContext.ChainedHeaderToValidate = new ChainedHeader(provenBlockHeader, provenBlockHeader.GetHash(), null);
            this.ruleContext.ValidationContext.ChainedHeaderToValidate.SetPrivatePropertyValue("Height", this.provenHeadersActivationHeight + 10);
            (this.ruleContext.ValidationContext.ChainedHeaderToValidate.Header as PosBlockHeader).ProvenBlockHeader.SetPrivateVariableValue<Transaction>("coinstake", null);

            // When we run the validation rule, we should hit coinstake empty exception.
            Action ruleValidation = () => this.consensusRules.RegisterRule<ProvenHeaderCoinstakeRule>().Run(this.ruleContext);
            ruleValidation.Should().Throw<ConsensusErrorException>()
                          .And.ConsensusError
                          .Should().Be(ConsensusErrors.EmptyCoinstake);
        }

        [Fact]
        public void RunRule_ProvenHeadersActive_And_CoinstakeUtxoIsEmpty_ReadTxPrevFailedErrorIsThrown()
        {
            // Setup proven header.
            PosBlock posBlock = new PosBlockBuilder(this.network).Build();
            ProvenBlockHeader provenBlockHeader = new ProvenBlockHeaderBuilder(posBlock, this.network).Build();
            if (provenBlockHeader.Coinstake is IPosTransactionWithTime posTrx)
                posTrx.Time = provenBlockHeader.Time;

            // Setup chained header and move it to the height higher than proven header activation height.
            this.ruleContext.ValidationContext.ChainedHeaderToValidate = new ChainedHeader(provenBlockHeader, provenBlockHeader.GetHash(), null);
            this.ruleContext.ValidationContext.ChainedHeaderToValidate.SetPrivatePropertyValue("Height", this.provenHeadersActivationHeight + 10);

            // By default no utxo are setup in coinview so fetch we return nothing.
            var res = new FetchCoinsResponse();
            res.UnspentOutputs.Add(new OutPoint(posBlock.Transactions[1].Inputs[0].PrevOut), null);
            this.coinView
                .Setup(m => m.FetchCoins(It.IsAny<OutPoint[]>()))
                .Returns(res);

            // When we run the validation rule, we should hit coinstake read transaction error.
            Action ruleValidation = () => this.consensusRules.RegisterRule<ProvenHeaderCoinstakeRule>().Run(this.ruleContext);
            ruleValidation.Should().Throw<ConsensusErrorException>()
                .And.ConsensusError
                .Should().Be(ConsensusErrors.ReadTxPrevFailedInsufficient);
        }

        [Fact]
        public void RunRule_ProvenHeadersActive_And_CoinstakeUnspentOutputsIsNull_ReadTxPrevFailedErrorIsThrown()
        {
            // Setup proven header.
            PosBlock posBlock = new PosBlockBuilder(this.network).Build();
            ProvenBlockHeader provenBlockHeader = new ProvenBlockHeaderBuilder(posBlock, this.network).Build();
            if (provenBlockHeader.Coinstake is IPosTransactionWithTime posTrx)
                posTrx.Time = provenBlockHeader.Time;

            // Setup chained header and move it to the height higher than proven header activation height.
            this.ruleContext.ValidationContext.ChainedHeaderToValidate = new ChainedHeader(provenBlockHeader, provenBlockHeader.GetHash(), null);
            this.ruleContext.ValidationContext.ChainedHeaderToValidate.SetPrivatePropertyValue("Height", this.provenHeadersActivationHeight + 10);

            // Add more null unspent output to coinstake.
            var res = new FetchCoinsResponse();
            res.UnspentOutputs.Add(posBlock.Transactions[1].Inputs[0].PrevOut, null);

            this.coinView
                .Setup(m => m.FetchCoins(It.IsAny<OutPoint[]>()))
                .Returns(res);

            // When we run the validation rule, we should hit coinstake read transaction error.
            Action ruleValidation = () => this.consensusRules.RegisterRule<ProvenHeaderCoinstakeRule>().Run(this.ruleContext);
            ruleValidation.Should().Throw<ConsensusErrorException>()
                .And.ConsensusError
                .Should().Be(ConsensusErrors.ReadTxPrevFailedInsufficient);
        }

        [Fact]
        public void RunRule_ProvenHeadersActive_And_CoinstakeIsIncorrectlySetup_NonCoinstakeErrorIsThrown()
        {
            // Setup proven header.
            PosBlock posBlock = new PosBlockBuilder(this.network).Build();
            ProvenBlockHeader provenBlockHeader = new ProvenBlockHeaderBuilder(posBlock, this.network).Build();

            // Setup chained header and move it to the height higher than proven header activation height.
            this.ruleContext.ValidationContext.ChainedHeaderToValidate = new ChainedHeader(provenBlockHeader, provenBlockHeader.GetHash(), null);
            this.ruleContext.ValidationContext.ChainedHeaderToValidate.SetPrivatePropertyValue("Height", this.provenHeadersActivationHeight + 10 + this.network.Consensus.LastPOWBlock);

            // Setup coinstake transaction.
            var res = new FetchCoinsResponse();
            res.UnspentOutputs.Add(new OutPoint(this.network.CreateTransaction(), 0), null);

            this.coinView
                .Setup(m => m.FetchCoins(It.IsAny<OutPoint[]>()))
                .Returns(res);

            // Change coinstake outputs to make it invalid.
            ((PosBlockHeader)this.ruleContext.ValidationContext.ChainedHeaderToValidate.Header).ProvenBlockHeader.Coinstake.Outputs.RemoveAt(0);

            Action ruleValidation = () => this.consensusRules.RegisterRule<ProvenHeaderCoinstakeRule>().Run(this.ruleContext);
            ruleValidation.Should().Throw<ConsensusErrorException>()
                .And.ConsensusError
                .Should().Be(ConsensusErrors.ProofOfWorkTooHigh);
        }

        [Fact]
        public void RunRule_ProvenHeadersActive_And_InvalidStakeTime_StakeTimeViolationErrorIsThrown()
        {
            // Setup proven header with valid coinstake.
            PosBlock posBlock = new PosBlockBuilder(this.network).Build();
            ProvenBlockHeader provenBlockHeader = new ProvenBlockHeaderBuilder(posBlock, this.network).Build();

            // Setup chained header and move it to the height higher than proven header activation height.
            this.ruleContext.ValidationContext.ChainedHeaderToValidate = new ChainedHeader(provenBlockHeader, provenBlockHeader.GetHash(), null);
            this.ruleContext.ValidationContext.ChainedHeaderToValidate.SetPrivatePropertyValue("Height", this.provenHeadersActivationHeight + 10);

            // Setup coinstake transaction.
            var res = new FetchCoinsResponse();
            res.UnspentOutputs.Add(new OutPoint(this.network.CreateTransaction(), 0), null);

            this.coinView
                .Setup(m => m.FetchCoins(It.IsAny<OutPoint[]>()))
                .Returns(res);

            // Change coinstake time to differ from header time but divisible by 16.
            ((PosBlockHeader)this.ruleContext.ValidationContext.ChainedHeaderToValidate.Header).Time = 16;

            // When we run the validation rule, we should hit coinstake stake time violation error.
            Action ruleValidation = () => this.consensusRules.RegisterRule<ProvenHeaderCoinstakeRule>().Run(this.ruleContext);
            ruleValidation.Should().Throw<ConsensusErrorException>()
                .And.ConsensusError
                .Should().Be(ConsensusErrors.StakeTimeViolation);

            // Change coinstake time to be the same as header time but not divisible by 16.
            this.ruleContext.ValidationContext.ChainedHeaderToValidate.Header.Time = 50;
            ((PosBlockHeader)this.ruleContext.ValidationContext.ChainedHeaderToValidate.Header).Time = 50;

            // When we run the validation rule, we should hit coinstake stake time violation error.
            ruleValidation.Should().Throw<ConsensusErrorException>()
                .And.ConsensusError
                .Should().Be(ConsensusErrors.StakeTimeViolation);
        }

        [Fact]
        public void RunRule_ProvenHeadersActive_And_InvalidStakeDepth_StakeDepthErrorIsThrown()
        {
            // Setup previous chained header.
            PosBlock prevPosBlock = new PosBlockBuilder(this.network).Build();
            ProvenBlockHeader prevProvenBlockHeader = new ProvenBlockHeaderBuilder(prevPosBlock, this.network).Build();
            var previousChainedHeader = new ChainedHeader(prevProvenBlockHeader, prevProvenBlockHeader.GetHash(), null);
            previousChainedHeader.SetPrivatePropertyValue("Height", this.provenHeadersActivationHeight + 1);

            // Setup proven header with valid coinstake.
            PosBlock posBlock = new PosBlockBuilder(this.network).Build();
            ProvenBlockHeader provenBlockHeader = new ProvenBlockHeaderBuilder(posBlock, this.network).Build();
            provenBlockHeader.PosBlockHeader.HashPrevBlock = prevProvenBlockHeader.GetHash();
            if (provenBlockHeader.Coinstake is IPosTransactionWithTime posTrx)
                posTrx.Time = provenBlockHeader.Time;

            // Setup chained header and move it to the height higher than proven header activation height.
            this.ruleContext.ValidationContext.ChainedHeaderToValidate = new ChainedHeader(provenBlockHeader, provenBlockHeader.GetHash(), previousChainedHeader);
            this.ruleContext.ValidationContext.ChainedHeaderToValidate.SetPrivatePropertyValue("Height", this.provenHeadersActivationHeight + 2);

            // Ensure that coinview returns a UTXO with valid outputs.
            var utxoOne = new UnspentOutput(prevPosBlock.Transactions[1].Inputs[0].PrevOut, new Coins((uint)previousChainedHeader.Height, new TxOut(), false, true));

            // Setup coinstake transaction with an invalid stake age.
            var res = new FetchCoinsResponse();
            res.UnspentOutputs.Add(utxoOne.OutPoint, utxoOne);

            this.coinView
                .Setup(m => m.FetchCoins(It.IsAny<OutPoint[]>()))
                .Returns(res);

            // Setup stake validator to fail stake age check.
            this.stakeValidator
                .Setup(m => m.IsConfirmedInNPrevBlocks(It.IsAny<UnspentOutput>(), It.IsAny<ChainedHeader>(), It.IsAny<long>()))
                .Returns(true);

            // When we run the validation rule, we should hit coinstake depth error.
            Action ruleValidation = () => this.consensusRules.RegisterRule<ProvenHeaderCoinstakeRule>().Run(this.ruleContext);
            ruleValidation.Should().Throw<ConsensusErrorException>()
                .And.ConsensusError
                .Should().Be(ConsensusErrors.InvalidStakeDepth);
        }

        [Fact]
        public void RunRule_ProvenHeadersActive_And_InvalidCoinstakeSignature_CoinstakeVerifySignatureErrorIsThrown()
        {
            // Setup previous chained header.
            PosBlock prevPosBlock = new PosBlockBuilder(this.network).Build();
            ProvenBlockHeader prevProvenBlockHeader = new ProvenBlockHeaderBuilder(prevPosBlock, this.network).Build();
            var previousChainedHeader = new ChainedHeader(prevProvenBlockHeader, prevProvenBlockHeader.GetHash(), null);
            previousChainedHeader.SetPrivatePropertyValue("Height", this.provenHeadersActivationHeight + 1);

            // Setup proven header with valid coinstake.
            PosBlock posBlock = new PosBlockBuilder(this.network).Build();
            ProvenBlockHeader provenBlockHeader = new ProvenBlockHeaderBuilder(posBlock, this.network).Build();
            provenBlockHeader.PosBlockHeader.HashPrevBlock = prevProvenBlockHeader.GetHash();
            if (provenBlockHeader.Coinstake is IPosTransactionWithTime posTrx)
                posTrx.Time = provenBlockHeader.Time;

            // Setup chained header and move it to the height higher than proven header activation height.
            this.ruleContext.ValidationContext.ChainedHeaderToValidate = new ChainedHeader(provenBlockHeader, provenBlockHeader.GetHash(), previousChainedHeader);
            this.ruleContext.ValidationContext.ChainedHeaderToValidate.SetPrivatePropertyValue("Height", this.provenHeadersActivationHeight + 2);

            // Ensure that coinview returns UTXO with valid outputs.
            var utxoOneTransaction = this.network.CreateTransaction();
            utxoOneTransaction.AddOutput(new TxOut());
            var utxoOne = new UnspentOutput(new OutPoint(utxoOneTransaction, 0), new Coins((uint)this.provenHeadersActivationHeight + 10, utxoOneTransaction.Outputs.First(), false));

            // Setup coinstake transaction with a valid stake age.
            var res = new FetchCoinsResponse();
            res.UnspentOutputs.Add(utxoOne.OutPoint, utxoOne);

            this.coinView
                .Setup(m => m.FetchCoins(It.IsAny<OutPoint[]>()))
                .Returns(res);

            // Setup stake validator to fail signature validation.
            this.stakeValidator
                .Setup(m => m.VerifySignature(It.IsAny<UnspentOutput>(), It.IsAny<Transaction>(), It.IsAny<int>(), It.IsAny<ScriptVerify>()))
                .Returns(false);

            // Setup stake validator to pass stake age check.
            this.stakeValidator
                .Setup(m => m.IsConfirmedInNPrevBlocks(It.IsAny<UnspentOutput>(), It.IsAny<ChainedHeader>(), It.IsAny<long>()))
                .Returns(false);

            // When we run the validation rule, we should hit coinstake signature verification error.
            Action ruleValidation = () => this.consensusRules.RegisterRule<ProvenHeaderCoinstakeRule>().Run(this.ruleContext);
            ruleValidation.Should().Throw<ConsensusErrorException>().And.ConsensusError.Should().Be(ConsensusErrors.CoinstakeVerifySignatureFailed);
        }

        [Fact]
        public void RunRule_ProvenHeadersActive_And_NullPreviousStake_InvalidPreviousProvenHeaderStakeModifierErrorIsThrown()
        {
            // Setup previous chained header.
            PosBlock prevPosBlock = new PosBlockBuilder(this.network).Build();
            ProvenBlockHeader prevProvenBlockHeader = new ProvenBlockHeaderBuilder(prevPosBlock, this.network).Build();
            prevProvenBlockHeader.StakeModifierV2 = null; // Forcing previous stake modifier to null.
            var previousChainedHeader = new ChainedHeader(prevProvenBlockHeader, prevProvenBlockHeader.GetHash(), null);
            previousChainedHeader.SetPrivatePropertyValue("Height", this.provenHeadersActivationHeight + 1);

            // Setup proven header with valid coinstake.
            PosBlock posBlock = new PosBlockBuilder(this.network).Build();
            ProvenBlockHeader provenBlockHeader = new ProvenBlockHeaderBuilder(posBlock, this.network).Build();
            provenBlockHeader.PosBlockHeader.HashPrevBlock = prevProvenBlockHeader.GetHash();
            if (provenBlockHeader.Coinstake is IPosTransactionWithTime posTrx)
                posTrx.Time = provenBlockHeader.Time;

            // Setup chained header and move it to the height higher than proven header activation height.
            this.ruleContext.ValidationContext.ChainedHeaderToValidate = new ChainedHeader(provenBlockHeader, provenBlockHeader.GetHash(), previousChainedHeader);
            this.ruleContext.ValidationContext.ChainedHeaderToValidate.SetPrivatePropertyValue("Height", this.provenHeadersActivationHeight + 2);

            // Ensure that coinview returns a UTXO with valid outputs.
            var utxoOneTransaction = this.network.CreateTransaction();
            utxoOneTransaction.AddOutput(new TxOut());
            var utxoOne = new UnspentOutput(new OutPoint(utxoOneTransaction, 0), new Coins((uint)this.provenHeadersActivationHeight + 10, utxoOneTransaction.Outputs.First(), false));

            // Setup coinstake transaction with a valid stake age.
            var res = new FetchCoinsResponse();
            res.UnspentOutputs.Add(utxoOne.OutPoint, utxoOne);

            this.coinView
                .Setup(m => m.FetchCoins(It.IsAny<OutPoint[]>()))
                .Returns(res);

            // Setup stake validator to pass stake age check.
            this.stakeValidator
                .Setup(m => m.IsConfirmedInNPrevBlocks(It.IsAny<UnspentOutput>(), It.IsAny<ChainedHeader>(), It.IsAny<long>()))
                .Returns(false);

            // Setup stake validator to pass signature validation.
            this.stakeValidator
                .Setup(m => m.VerifySignature(It.IsAny<UnspentOutput>(), It.IsAny<Transaction>(), It.IsAny<int>(), It.IsAny<ScriptVerify>()))
                .Returns(true);

            // When we run the validation rule, we should hit previous stake null error.
            Action ruleValidation = () => this.consensusRules.RegisterRule<ProvenHeaderCoinstakeRule>().Run(this.ruleContext);
            ruleValidation.Should().Throw<ConsensusErrorException>()
                .And.ConsensusError
                .Should().Be(ConsensusErrors.InvalidPreviousProvenHeaderStakeModifier);
        }

        [Fact]
        public void RunRule_ProvenHeadersActive_And_InvalidStakeKernelHash_CoinstakeVerifySignatureErrorIsThrown()
        {
            // Setup previous chained header.
            PosBlock prevPosBlock = new PosBlockBuilder(this.network).Build();
            ProvenBlockHeader prevProvenBlockHeader = new ProvenBlockHeaderBuilder(prevPosBlock, this.network).Build();
            var previousChainedHeader = new ChainedHeader(prevProvenBlockHeader, prevProvenBlockHeader.GetHash(), null);
            previousChainedHeader.SetPrivatePropertyValue("Height", this.provenHeadersActivationHeight + 1);

            // Setup proven header with valid coinstake.
            PosBlock posBlock = new PosBlockBuilder(this.network).Build();
            ProvenBlockHeader provenBlockHeader = new ProvenBlockHeaderBuilder(posBlock, this.network).Build(prevProvenBlockHeader);
            provenBlockHeader.PosBlockHeader.HashPrevBlock = prevProvenBlockHeader.GetHash();
            if (provenBlockHeader.Coinstake is IPosTransactionWithTime posTrx)
                posTrx.Time = provenBlockHeader.Time;

            // Setup chained header and move it to the height higher than proven header activation height.
            this.ruleContext.ValidationContext.ChainedHeaderToValidate = new ChainedHeader(provenBlockHeader, provenBlockHeader.GetHash(), previousChainedHeader);
            this.ruleContext.ValidationContext.ChainedHeaderToValidate.SetPrivatePropertyValue("Height", this.provenHeadersActivationHeight + 2);

            // Ensure that coinview returns a UTXO with valid outputs.
            var utxoOneTransaction = this.network.CreateTransaction();
            utxoOneTransaction.AddOutput(new TxOut());
            var utxoOne = new UnspentOutput(new OutPoint(utxoOneTransaction, 0), new Coins((uint)this.provenHeadersActivationHeight + 10, utxoOneTransaction.Outputs.First(), false));

            // Setup coinstake transaction with a valid stake age.
            var res = new FetchCoinsResponse();
            res.UnspentOutputs.Add(utxoOne.OutPoint, utxoOne);

            this.coinView
                .Setup(m => m.FetchCoins(It.IsAny<OutPoint[]>()))
                .Returns(res);

            // Setup stake validator to pass stake age check.
            this.stakeValidator
                .Setup(m => m.IsConfirmedInNPrevBlocks(It.IsAny<UnspentOutput>(), It.IsAny<ChainedHeader>(), It.IsAny<long>()))
                .Returns(false);

            // Setup stake validator to pass signature validation.
            this.stakeValidator
                .Setup(m => m.VerifySignature(It.IsAny<UnspentOutput>(), It.IsAny<Transaction>(), It.IsAny<int>(), It.IsAny<ScriptVerify>()))
                .Returns(true);

            // Setup stake validator to fail stake kernel hash validation.
            this.stakeChain.Setup(m => m.Get(It.IsAny<uint256>())).Returns(new BlockStake());
            this.stakeValidator
                .Setup(m => m.CheckStakeKernelHash(It.IsAny<PosRuleContext>(), It.IsAny<uint>(), It.IsAny<uint256>(), It.IsAny<UnspentOutput>(), It.IsAny<OutPoint>(), It.IsAny<uint>()))
                .Throws(new ConsensusErrorException(ConsensusErrors.StakeHashInvalidTarget));

            // When we run the validation rule, we should hit stake hash invalid target error.
            Action ruleValidation = () => this.consensusRules.RegisterRule<ProvenHeaderCoinstakeRule>().Run(this.ruleContext);
            ruleValidation.Should().Throw<ConsensusErrorException>()
                .And.ConsensusError
                .Should().Be(ConsensusErrors.StakeHashInvalidTarget);
        }

        [Fact]
        [Trait("Unstable", "True")]
        public void RunRule_ProvenHeadersActive_And_InvalidMerkleProof_BadMerkleProofErrorIsThrown()
        {
            // Setup previous chained header.
            PosBlock prevPosBlock = new PosBlockBuilder(this.network).Build();
            ProvenBlockHeader prevProvenBlockHeader = new ProvenBlockHeaderBuilder(prevPosBlock, this.network).Build();
            var previousChainedHeader = new ChainedHeader(prevProvenBlockHeader, prevProvenBlockHeader.GetHash(), null);
            previousChainedHeader.SetPrivatePropertyValue("Height", this.provenHeadersActivationHeight + 1);

            // Setup proven header with valid coinstake.
            PosBlock posBlock = new PosBlockBuilder(this.network).Build();
            ProvenBlockHeader provenBlockHeader = new ProvenBlockHeaderBuilder(posBlock, this.network).Build(prevProvenBlockHeader);
            provenBlockHeader.PosBlockHeader.HashPrevBlock = prevProvenBlockHeader.GetHash();
            if (provenBlockHeader.Coinstake is IPosTransactionWithTime posTrx)
                posTrx.Time = provenBlockHeader.Time;

            // Corrupt merkle proof.
            provenBlockHeader.SetPrivateVariableValue("merkleProof", new PartialMerkleTree(new[] { new uint256(1234) }, new[] { false }));

            // Setup chained header and move it to the height higher than proven header activation height.
            this.ruleContext.ValidationContext.ChainedHeaderToValidate = new ChainedHeader(provenBlockHeader, provenBlockHeader.GetHash(), previousChainedHeader);
            this.ruleContext.ValidationContext.ChainedHeaderToValidate.SetPrivatePropertyValue("Height", this.provenHeadersActivationHeight + 2);

            // Ensure that coinview returns a UTXO with valid outputs.
            var utxoOne = new UnspentOutput(prevPosBlock.Transactions[1].Inputs[0].PrevOut, new Coins((uint)previousChainedHeader.Height, new TxOut(), false, true));

            // Setup coinstake transaction with a valid stake age.
            var res = new FetchCoinsResponse();
            res.UnspentOutputs.Add(utxoOne.OutPoint, utxoOne);

            this.coinView
                .Setup(m => m.FetchCoins(It.IsAny<OutPoint[]>()))
                .Returns(res);

            // Setup stake validator to pass stake age check.
            this.stakeValidator
                .Setup(m => m.IsConfirmedInNPrevBlocks(It.IsAny<UnspentOutput>(), It.IsAny<ChainedHeader>(), It.IsAny<long>()))
                .Returns(false);

            // Setup stake validator to pass signature validation.
            this.stakeValidator
                .Setup(m => m.VerifySignature(It.IsAny<UnspentOutput>(), It.IsAny<Transaction>(), It.IsAny<int>(), It.IsAny<ScriptVerify>()))
                .Returns(true);

            // Setup stake validator to pass stake kernel hash validation.
            this.stakeChain.Setup(m => m.Get(It.IsAny<uint256>())).Returns(new BlockStake());
            this.stakeValidator
                .Setup(m => m.CheckStakeKernelHash(It.IsAny<PosRuleContext>(), It.IsAny<uint>(), It.IsAny<uint256>(), It.IsAny<UnspentOutput>(), It.IsAny<OutPoint>(), It.IsAny<uint>())).Returns(true);

            // When we run the validation rule, we should hit bad merkle proof error.
            Action ruleValidation = () => this.consensusRules.RegisterRule<ProvenHeaderCoinstakeRule>().Run(this.ruleContext);
            ruleValidation.Should().Throw<ConsensusErrorException>()
                .And.ConsensusError
                .Should().Be(ConsensusErrors.BadMerkleRoot);
        }

        [Fact]
        public void RunRule_ProvenHeadersActive_And_InvalidCoinstakeKernelSignature_BadBlockSignatureErrorIsThrown()
        {
            // Setup private key.
            var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve);
            Key privateKey = mnemonic.DeriveExtKey().PrivateKey;

            // Setup previous chained header.
            PosBlock prevPosBlock = new PosBlockBuilder(this.network, privateKey).Build();
            ProvenBlockHeader prevProvenBlockHeader = new ProvenBlockHeaderBuilder(prevPosBlock, this.network).Build();
            var previousChainedHeader = new ChainedHeader(prevProvenBlockHeader, prevProvenBlockHeader.GetHash(), null);
            previousChainedHeader.SetPrivatePropertyValue("Height", this.provenHeadersActivationHeight + 1);

            // Setup proven header with valid coinstake.
            PosBlock posBlock = new PosBlockBuilder(this.network, privateKey).Build();
            posBlock.UpdateMerkleRoot();
            ProvenBlockHeader provenBlockHeader = new ProvenBlockHeaderBuilder(posBlock, this.network).Build(prevProvenBlockHeader);
            provenBlockHeader.PosBlockHeader.HashPrevBlock = prevProvenBlockHeader.GetHash();
            if (provenBlockHeader.Coinstake is IPosTransactionWithTime posTrx)
                posTrx.Time = provenBlockHeader.Time;

            // Set invalid coinstake script pub key.
            provenBlockHeader.Coinstake.Outputs[1].ScriptPubKey = new Script("03cdac179a3391d96cf4957fa0255e4aa8055a993e92df7146e740117885b184ea OP_CHECKSIG");

            // Setup chained header and move it to the height higher than proven header activation height.
            this.ruleContext.ValidationContext.ChainedHeaderToValidate = new ChainedHeader(provenBlockHeader, provenBlockHeader.GetHash(), previousChainedHeader);
            this.ruleContext.ValidationContext.ChainedHeaderToValidate.SetPrivatePropertyValue("Height", this.provenHeadersActivationHeight + 2);

            // Setup coinstake transaction with a valid stake age.
            uint unspentOutputsHeight = (uint)this.provenHeadersActivationHeight + 10;

            var res = new FetchCoinsResponse();
            var unspentOutputs = new UnspentOutput(prevPosBlock.Transactions[1].Inputs[0].PrevOut,
                new Coins(unspentOutputsHeight, new TxOut(new Money(100), privateKey.PubKey), false));

            res.UnspentOutputs.Add(unspentOutputs.OutPoint, unspentOutputs);

            this.coinView
                .Setup(m => m.FetchCoins(It.IsAny<OutPoint[]>()))
                .Returns(res);

            // Setup stake validator to pass signature validation.
            this.stakeValidator
                .Setup(m => m.VerifySignature(It.IsAny<UnspentOutput>(), It.IsAny<Transaction>(), It.IsAny<int>(), It.IsAny<ScriptVerify>()))
                .Returns(true);

            // Setup stake validator to pass stake kernel hash validation.
            this.stakeChain.Setup(m => m.Get(It.IsAny<uint256>())).Returns(new BlockStake());
            this.stakeValidator
                .Setup(m => m.CheckStakeKernelHash(It.IsAny<PosRuleContext>(), It.IsAny<uint>(), It.IsAny<uint256>(), It.IsAny<UnspentOutput>(), It.IsAny<OutPoint>(), It.IsAny<uint>())).Returns(true);

            // Setup stake validator to pass stake age check.
            this.stakeValidator
                .Setup(m => m.IsConfirmedInNPrevBlocks(It.IsAny<UnspentOutput>(), It.IsAny<ChainedHeader>(), It.IsAny<long>()))
                .Returns(false);

            // When we run the validation rule, we should hit bad merkle proof error.
            Action ruleValidation = () => this.consensusRules.RegisterRule<ProvenHeaderCoinstakeRule>().Run(this.ruleContext);
            ruleValidation.Should().Throw<ConsensusErrorException>()
                .And.ConsensusError
                .Should().Be(ConsensusErrors.BadBlockSignature);
        }

        [Fact]
        public void RunRule_ProvenHeadersActive_And_ValidProvenHeader_NoErrorsAreThrown()
        {
            // Setup private key.
            var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve);
            Key privateKey = mnemonic.DeriveExtKey().PrivateKey;

            // Setup previous chained header.
            PosBlock prevPosBlock = new PosBlockBuilder(this.network, privateKey).Build();
            ProvenBlockHeader prevProvenBlockHeader = new ProvenBlockHeaderBuilder(prevPosBlock, this.network).Build();
            var previousChainedHeader = new ChainedHeader(prevProvenBlockHeader, prevProvenBlockHeader.GetHash(), null);
            previousChainedHeader.SetPrivatePropertyValue("Height", this.provenHeadersActivationHeight + 1);

            // Setup proven header with valid coinstake.
            PosBlock posBlock = new PosBlockBuilder(this.network, privateKey).Build();
            posBlock.UpdateMerkleRoot();
            posBlock.Header.HashPrevBlock = prevProvenBlockHeader.GetHash();
            posBlock.Header.Bits = 16777216;

            // Update signature.
            ECDSASignature signature = privateKey.Sign(posBlock.Header.GetHash());
            posBlock.BlockSignature = new BlockSignature { Signature = signature.ToDER() };

            ProvenBlockHeader provenBlockHeader = new ProvenBlockHeaderBuilder(posBlock, this.network).Build(prevProvenBlockHeader);
            provenBlockHeader.PosBlockHeader.HashPrevBlock = prevProvenBlockHeader.GetHash();
            if (provenBlockHeader.Coinstake is IPosTransactionWithTime posTrx)
                posTrx.Time = provenBlockHeader.Time;

            // Set invalid coinstake script pub key
            provenBlockHeader.Coinstake.Outputs[1].ScriptPubKey = privateKey.PubKey.ScriptPubKey;

            // Setup chained header and move it to the height higher than proven header activation height.
            this.ruleContext.ValidationContext.ChainedHeaderToValidate = new ChainedHeader(provenBlockHeader, provenBlockHeader.GetHash(), previousChainedHeader);
            this.ruleContext.ValidationContext.ChainedHeaderToValidate.SetPrivatePropertyValue("Height", this.provenHeadersActivationHeight + 2);

            // Setup coinstake transaction with a valid stake age.

            uint unspentOutputsHeight = (uint)this.provenHeadersActivationHeight + 10;
            var res = new FetchCoinsResponse();
            var unspentOutputs = new UnspentOutput(prevPosBlock.Transactions[1].Inputs[0].PrevOut,
                new Coins(unspentOutputsHeight, new TxOut(new Money(100), privateKey.PubKey), false));

            res.UnspentOutputs.Add(unspentOutputs.OutPoint, unspentOutputs);

            this.coinView
                .Setup(m => m.FetchCoins(It.IsAny<OutPoint[]>()))
                .Returns(res);

            // Setup stake validator to pass stake age check.
            this.stakeValidator
                .Setup(m => m.IsConfirmedInNPrevBlocks(It.IsAny<UnspentOutput>(), It.IsAny<ChainedHeader>(), It.IsAny<long>()))
                .Returns(false);

            // Setup stake validator to pass signature validation.
            this.stakeValidator
                .Setup(m => m.VerifySignature(It.IsAny<UnspentOutput>(), It.IsAny<Transaction>(), It.IsAny<int>(), It.IsAny<ScriptVerify>()))
                .Returns(true);

            // Setup stake validator to pass stake kernel hash validation.
            this.stakeChain.Setup(m => m.Get(It.IsAny<uint256>())).Returns(new BlockStake());
            this.stakeValidator
                .Setup(m => m.CheckStakeKernelHash(It.IsAny<PosRuleContext>(), It.IsAny<uint>(), It.IsAny<uint256>(), It.IsAny<UnspentOutput>(), It.IsAny<OutPoint>(), It.IsAny<uint>())).Returns(true);

            // When we run the validation rule, we should not hit any errors.
            Action ruleValidation = () => this.consensusRules.RegisterRule<ProvenHeaderCoinstakeRule>().Run(this.ruleContext);
            ruleValidation.Should().NotThrow();
        }
    }
}