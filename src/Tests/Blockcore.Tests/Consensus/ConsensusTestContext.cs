﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Blockcore.AsyncWork;
using Blockcore.Base;
using Blockcore.Base.Deployments;
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
using Blockcore.Features.Consensus.Rules;
using Blockcore.Features.Consensus.Rules.CommonRules;
using Blockcore.Interfaces;
using Blockcore.NBitcoin;
using Blockcore.NBitcoin.BouncyCastle.math;
using Blockcore.Networks;
using Blockcore.P2P;
using Blockcore.P2P.Peer;
using Blockcore.P2P.Protocol.Payloads;
using Blockcore.Signals;
using Blockcore.Tests.Common;
using Blockcore.Utilities;
using Microsoft.Extensions.Logging;
using Moq;
using NBitcoin;

using Xunit;
using static Blockcore.Tests.Consensus.ChainedHeaderTreeTest;

namespace Blockcore.Tests.Consensus
{
    public class TestContext
    {
        public Mock<IHeaderValidator> HeaderValidator { get; }

        public Network Network;

        internal ChainedHeaderTree ChainedHeaderTree;
        private readonly INodeStats nodeStats;
        private readonly Mock<IInitialBlockDownloadState> ibd;
        public readonly Mock<IBlockPuller> BlockPuller;
        public readonly Mock<IBlockStore> BlockStore;
        private readonly Mock<ICheckpoints> checkpoints = new Mock<ICheckpoints>();
        public TestConsensusManager TestConsensusManager;
        public Mock<IFinalizedBlockInfoRepository> FinalizedBlockMock = new Mock<IFinalizedBlockInfoRepository>();
        public readonly Mock<IInitialBlockDownloadState> ibdState = new Mock<IInitialBlockDownloadState>();
        internal ChainedHeader InitialChainTip;
        public Mock<IIntegrityValidator> IntegrityValidator = new Mock<IIntegrityValidator>();
        public readonly Mock<IPartialValidator> PartialValidator;
        public readonly Mock<IFullValidator> FullValidator;
        public BlockPuller.OnBlockDownloadedCallback blockPullerBlockDownloadCallback;
        private readonly IPeerBanning peerBanning;
        private readonly IConnectionManager connectionManager;
        private static int nonceValue;
        internal ChainIndexer chainIndexer;
        private readonly DateTimeProvider dateTimeProvider;
        private readonly InvalidBlockHashStore hashStore;
        private readonly NodeSettings nodeSettings;
        private readonly ILoggerFactory loggerFactory;
        public ConsensusSettings ConsensusSettings;
        private readonly INetworkPeerFactory networkPeerFactory;
        public Mock<IChainState> ChainState;
        private readonly IConsensusRuleEngine consensusRules;
        public readonly TestInMemoryCoinView coinView;
        private readonly NodeDeployments deployments;
        private readonly ISelfEndpointTracker selfEndpointTracker;
        private readonly INodeLifetime nodeLifetime;

        private readonly PeerAddressManager peerAddressManager;
        private readonly ISignals signals;
        private readonly IAsyncProvider asyncProvider;

        public TestContext()
        {
            this.Network = KnownNetworks.RegTest;

            this.chainIndexer = new ChainIndexer(this.Network);
            this.dateTimeProvider = new DateTimeProvider();
            this.hashStore = new InvalidBlockHashStore(this.dateTimeProvider);

            this.coinView = new TestInMemoryCoinView(new HashHeightPair(this.chainIndexer.Tip));
            this.HeaderValidator = new Mock<IHeaderValidator>();
            this.HeaderValidator.Setup(hv => hv.ValidateHeader(It.IsAny<ChainedHeader>())).Returns(new ValidationContext());

            this.nodeLifetime = new NodeLifetime();
            this.ibd = new Mock<IInitialBlockDownloadState>();
            this.BlockPuller = new Mock<IBlockPuller>();

            this.BlockPuller.Setup(b => b.Initialize(It.IsAny<BlockPuller.OnBlockDownloadedCallback>()))
                .Callback<BlockPuller.OnBlockDownloadedCallback>((d) => { this.blockPullerBlockDownloadCallback = d; });
            this.BlockStore = new Mock<IBlockStore>();
            this.checkpoints = new Mock<ICheckpoints>();
            this.ChainState = new Mock<IChainState>();

            string[] param = new string[] { };
            this.nodeSettings = new NodeSettings(this.Network, args: param);
            this.ConsensusSettings = new ConsensusSettings(this.nodeSettings);

            this.loggerFactory = this.nodeSettings.LoggerFactory;

            this.nodeStats = new NodeStats(this.dateTimeProvider, this.loggerFactory);

            var connectionSettings = new ConnectionManagerSettings(this.nodeSettings);
            this.selfEndpointTracker = new SelfEndpointTracker(this.loggerFactory, connectionSettings);
            this.Network.Consensus.Options = new ConsensusOptions();

            this.signals = new Blockcore.Signals.Signals(this.loggerFactory, null);
            this.asyncProvider = new AsyncProvider(this.loggerFactory, this.signals, this.nodeLifetime);

            // Dont check PoW of a header in this test.
            this.Network.Consensus.ConsensusRules.HeaderValidationRules.RemoveAll(x => x.GetType() == typeof(CheckDifficultyPowRule));

            this.ChainedHeaderTree = new ChainedHeaderTree(
                  this.Network,
                  this.loggerFactory,
                  this.HeaderValidator.Object,
                  this.checkpoints.Object,
                  this.ChainState.Object,
                  this.FinalizedBlockMock.Object,
                  this.ConsensusSettings,
                  this.hashStore);

            this.peerAddressManager = new PeerAddressManager(DateTimeProvider.Default, this.nodeSettings.DataFolder, this.loggerFactory, this.selfEndpointTracker);

            this.networkPeerFactory = new NetworkPeerFactory(this.Network,
                this.dateTimeProvider,
                this.loggerFactory, new PayloadProvider().DiscoverPayloads(),
                this.selfEndpointTracker,
                this.ibd.Object,
                new ConnectionManagerSettings(this.nodeSettings),
                this.asyncProvider,
                this.peerAddressManager);

            var peerDiscovery = new PeerDiscovery(this.asyncProvider, this.loggerFactory, this.Network, this.networkPeerFactory, this.nodeLifetime, this.nodeSettings, this.peerAddressManager);

            this.connectionManager = new ConnectionManager(this.dateTimeProvider, this.loggerFactory, this.Network, this.networkPeerFactory, this.nodeSettings,
                this.nodeLifetime, new NetworkPeerConnectionParameters(), this.peerAddressManager, new IPeerConnector[] { },
                peerDiscovery, this.selfEndpointTracker, connectionSettings, new VersionProvider(), this.nodeStats, this.asyncProvider);

            this.deployments = new NodeDeployments(this.Network, this.chainIndexer);

            this.consensusRules = new PowConsensusRuleEngine(this.Network, this.loggerFactory, this.dateTimeProvider, this.chainIndexer, this.deployments, this.ConsensusSettings,
                     this.checkpoints.Object, this.coinView, this.ChainState.Object, this.hashStore, this.nodeStats, this.asyncProvider, new ConsensusRulesContainer());

            this.consensusRules.SetupRulesEngineParent();

            var tree = new ChainedHeaderTree(this.Network, this.loggerFactory, this.HeaderValidator.Object, this.checkpoints.Object,
                this.ChainState.Object, this.FinalizedBlockMock.Object, this.ConsensusSettings, this.hashStore);

            this.PartialValidator = new Mock<IPartialValidator>();
            this.FullValidator = new Mock<IFullValidator>();

            this.peerBanning = new PeerBanning(this.connectionManager, this.loggerFactory, this.dateTimeProvider, this.peerAddressManager);

            this.IntegrityValidator.Setup(i => i.VerifyBlockIntegrity(It.IsAny<ChainedHeader>(), It.IsAny<Block>()))
                .Returns(new ValidationContext());

            ConsensusManager consensusManager = new ConsensusManager(tree, this.Network, this.loggerFactory, this.ChainState.Object, this.IntegrityValidator.Object,
                this.PartialValidator.Object, this.FullValidator.Object, this.consensusRules,
                this.FinalizedBlockMock.Object, this.signals, this.peerBanning, this.ibd.Object, this.chainIndexer,
                this.BlockPuller.Object, this.BlockStore.Object, this.connectionManager, this.nodeStats, this.nodeLifetime, this.ConsensusSettings, this.dateTimeProvider);

            this.TestConsensusManager = new TestConsensusManager(consensusManager);
        }

        public Block CreateBlock(ChainedHeader previous)
        {
            Block block = this.Network.CreateBlock();
            block.AddTransaction(this.Network.CreateTransaction());
            block.AddTransaction(this.Network.CreateTransaction());
            block.Transactions[0].AddInput(new TxIn(Script.Empty));
            block.Transactions[0].AddOutput(Money.COIN + 10, Script.Empty);
            block.GetSerializedSize();
            block.UpdateMerkleRoot();

            block.Header.HashPrevBlock = previous.HashBlock;

            return block;
        }

        internal Target ChangeDifficulty(ChainedHeader header, int difficultyAdjustmentDivisor)
        {
            var newTarget = header.Header.Bits.ToBigInteger();
            newTarget = newTarget.Divide(BigInteger.ValueOf(difficultyAdjustmentDivisor));
            return new Target(newTarget);
        }

        public void SetupCheckpoints(params CheckpointFixture[] checkpoints)
        {
            if (checkpoints.GroupBy(h => h.Height).Any(g => g.Count() > 1))
                throw new ArgumentException("Checkpoint heights must be unique.");

            if (checkpoints.Any(h => h.Height < 0))
                throw new ArgumentException("Checkpoint heights cannot be negative.");

            foreach (CheckpointFixture checkpoint in checkpoints.OrderBy(h => h.Height))
            {
                var checkpointInfo = new CheckpointInfo(checkpoint.Header.GetHash());
                this.checkpoints
                    .Setup(c => c.GetCheckpoint(checkpoint.Height))
                    .Returns(checkpointInfo);
            }

            this.checkpoints
                .Setup(c => c.GetCheckpoint(It.IsNotIn(checkpoints.Select(h => h.Height))))
                .Returns((CheckpointInfo)null);

            this.checkpoints
                .Setup(c => c.LastCheckpointHeight)
                .Returns(checkpoints.OrderBy(h => h.Height).Last().Height);

            this.checkpoints
                .Setup(c => c.LastCheckpointHeight)
                .Returns(checkpoints.OrderBy(h => h.Height).Last().Height);
        }

        public ChainedHeader ExtendAChain(
            int count,
            ChainedHeader chainedHeader = null,
            int difficultyAdjustmentDivisor = 1,
            bool assignBlocks = true,
            ValidationState? validationState = null,
            int? avgBlockSize = null)
        {
            if (difficultyAdjustmentDivisor == 0)
                throw new ArgumentException("Divisor cannot be 0");

            ChainedHeader previousHeader = chainedHeader ?? new ChainedHeader(this.Network.GetGenesis().Header, this.Network.GenesisHash, 0);

            for (int i = 0; i < count; i++)
            {
                BlockHeader header = this.Network.Consensus.ConsensusFactory.CreateBlockHeader();
                header.HashPrevBlock = previousHeader.HashBlock;
                header.Bits = difficultyAdjustmentDivisor == 1
                                    ? previousHeader.Header.Bits
                                    : this.ChangeDifficulty(previousHeader, difficultyAdjustmentDivisor);
                header.Nonce = (uint)Interlocked.Increment(ref nonceValue);

                var newHeader = new ChainedHeader(header, header.GetHash(), previousHeader);

                if (validationState.HasValue)
                    newHeader.BlockValidationState = validationState.Value;

                if (assignBlocks)
                {
                    Block block = this.Network.Consensus.ConsensusFactory.CreateBlock();
                    block.Header.Bits = header.Bits;
                    block.Header.HashPrevBlock = header.HashPrevBlock;
                    block.Header.Nonce = header.Nonce;

                    block.GetSerializedSize();

                    if (avgBlockSize.HasValue)
                    {
                        var transaction = new Transaction();
                        transaction.Outputs.Add(new TxOut(new Money(10000000000), new Script()));
                        block.Transactions.Add(transaction);

                        int blockWeight = block.GetSerializedSize();

                        int requiredScriptWeight = avgBlockSize.Value - blockWeight;
                        block.Transactions[0].Outputs.Clear();
                        // generate nonsense script with required bytes to reach required weight.
                        Script script = Script.FromBytesUnsafe(new string('A', requiredScriptWeight).Select(c => (byte)c).ToArray());
                        transaction.Outputs.Add(new TxOut(new Money(10000000000), script));

                        this.ResetBlockSize(block);
                        block.GetSerializedSize();

                        if (block.BlockSize != avgBlockSize.Value)
                        {
                            throw new Exception("Unable to generate block with expected size.");
                        }
                    }


                    newHeader.Block = block;
                }

                previousHeader = newHeader;
            }

            return previousHeader;
        }

        public void ResetBlockSize(Block block)
        {
            block.SetPrivatePropertyValue("BlockSize", default(long?));
        }

        public Block CreateBlock()
        {
            Block block = this.Network.CreateBlock();
            block.GetSerializedSize();
            return block;
        }

        public List<BlockHeader> ChainedHeaderToList(ChainedHeader chainedHeader, int count)
        {
            var list = new List<BlockHeader>();

            ChainedHeader current = chainedHeader;

            for (int i = 0; i < count; i++)
            {
                list.Add(current.Header);
                current = current.Previous;
            }

            list.Reverse();

            return list;
        }

        public bool NoDownloadRequested(ConnectNewHeadersResult connectNewHeadersResult)
        {
            Assert.NotNull(connectNewHeadersResult);

            return (connectNewHeadersResult.DownloadTo == null)
                   && (connectNewHeadersResult.DownloadFrom == null);
        }

        internal void SetupAverageBlockSize(int amount)
        {
            this.BlockPuller.Setup(b => b.GetAverageBlockSizeBytes())
                .Returns(amount);
        }


        internal void VerifyNoBlocksAskedToBlockPuller()
        {
            this.BlockPuller.Verify(b => b.RequestBlocksDownload(It.IsAny<List<ChainedHeader>>(), It.IsAny<bool>()), Times.Exactly(0));
        }

        internal void AssertPeerBanned(INetworkPeer peer)
        {
            Assert.True(this.peerBanning.IsBanned(peer.PeerEndPoint));
        }

        internal void AssertExpectedBlockSizesEmpty()
        {
            Assert.Empty(this.TestConsensusManager.GetExpectedBlockSizes());
        }

        internal void AssertExpectedBlockSizes(uint expectedSize)
        {
            Assert.Equal(expectedSize, this.TestConsensusManager.GetExpectedBlockSizes().Sum(s => s.Value));
        }

        internal Mock<INetworkPeer> GetNetworkPeerWithConnection()
        {
            var networkPeer = new Mock<INetworkPeer>();

            var signals = new Blockcore.Signals.Signals(this.loggerFactory, null);
            var asyncProvider = new AsyncProvider(this.loggerFactory, this.signals, new NodeLifetime());

            var connection = new NetworkPeerConnection(this.Network, networkPeer.Object, new TcpClient(), 0, (message, token) => Task.CompletedTask,
            this.dateTimeProvider, this.loggerFactory, new PayloadProvider().DiscoverPayloads(), asyncProvider);
            networkPeer.Setup(n => n.Connection)
                .Returns(connection);

            networkPeer.Setup(n => n.PeerEndPoint)
                .Returns(new System.Net.IPEndPoint(IPAddress.Loopback, 9999));

            networkPeer.Setup(n => n.RemoteSocketAddress)
                .Returns(IPAddress.Loopback.EnsureIPv6());
            networkPeer.Setup(n => n.RemoteSocketPort)
                .Returns(9999);

            networkPeer.Setup(n => n.RemoteSocketEndpoint)
                .Returns(new System.Net.IPEndPoint(IPAddress.Loopback.EnsureIPv6(), 9999));

            networkPeer.Setup(n => n.State)
                .Returns(NetworkPeerState.Connected);

            var behavior = new Mock<IConnectionManagerBehavior>();
            networkPeer.Setup(n => n.Behavior<IConnectionManagerBehavior>())
                .Returns(behavior.Object);

            this.peerAddressManager.AddPeer(networkPeer.Object.PeerEndPoint, networkPeer.Object.PeerEndPoint.Address);
            this.connectionManager.AddConnectedPeer(networkPeer.Object);

            return networkPeer;
        }

        /// <summary>
        /// Initial setup for tests 18-20, 28.
        /// Chain header tree setup. Initial chain has 4 headers.
        /// SetUp:
        ///                        =8d=9d=10d
        ///                   6a=7a=8a=9a
        /// GENESIS=1=2=3=4=5=
        ///                   6b=7b=8b=9b
        ///             3c=4c=5c
        /// </summary>
        /// <param name="cht">ChainHeaderTree.</param>
        /// <param name="initialChainTip">Initial chain tip.</param>
        internal void SetupPeersForTest(ChainedHeaderTree cht, ChainedHeader initialChainTip)
        {
            int peerAExtension = 4;
            int peerBExtension = 4;
            int peerCExtension = 3;
            int peerDExtension = 3;

            ChainedHeader chainATip = this.ExtendAChain(peerAExtension, initialChainTip); // i.e. (h1=h2=h3=h4=h5)=6a=7a=8a=9a
            ChainedHeader chainBTip = this.ExtendAChain(peerBExtension, initialChainTip); // i.e. (h1=h2=h3=h4=h5)=6b=7b=8b=9b
            ChainedHeader chainCTip = this.ExtendAChain(peerCExtension, initialChainTip.GetAncestor(2)); // i.e. (h1=h2)=3c=4c=5c
            ChainedHeader chainDTip = this.ExtendAChain(peerDExtension, chainATip.GetAncestor(7)); // i.e. ((h1=h2=h3=h4=h5)=6a=7a)=8d=9d=10d

            List<BlockHeader> peerABlockHeaders = this.ChainedHeaderToList(chainATip, chainATip.Height);
            List<BlockHeader> peerBBlockHeaders = this.ChainedHeaderToList(chainBTip, chainBTip.Height);
            List<BlockHeader> peerCBlockHeaders = this.ChainedHeaderToList(chainCTip, chainCTip.Height);
            List<BlockHeader> peerDBlockHeaders = this.ChainedHeaderToList(chainDTip, chainDTip.Height);

            cht.ConnectNewHeaders(0, peerABlockHeaders);
            cht.ConnectNewHeaders(1, peerBBlockHeaders);
            cht.ConnectNewHeaders(2, peerCBlockHeaders);
            cht.ConnectNewHeaders(3, peerDBlockHeaders);
        }

        internal void SwitchToChain(ChainedHeaderTree cht, ChainedHeader chainTip, ChainedHeader consumedHeader, int extensionSize)
        {
            ChainedHeader[] consumedHeaders = consumedHeader.ToArray(extensionSize);

            for (int i = 0; i < extensionSize; i++)
            {
                ChainedHeader currentConsumedCh = consumedHeaders[i];
                cht.BlockDataDownloaded(currentConsumedCh, chainTip.GetAncestor(currentConsumedCh.Height).Block);
                cht.PartialValidationSucceeded(currentConsumedCh, out bool fullValidationRequired);
                cht.ConsensusTipChanged(currentConsumedCh);
            }
        }
    }
}
