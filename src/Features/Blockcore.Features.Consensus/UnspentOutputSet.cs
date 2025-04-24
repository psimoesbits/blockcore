using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Blockcore.Consensus.BlockInfo;
using Blockcore.Consensus.TransactionInfo;
using Blockcore.NBitcoin;
using Blockcore.Networks;
using Blockcore.Utilities;

namespace Blockcore.Features.Consensus
{
    public class UnspentOutputSet
    {
        private readonly int degreeOfParallelism = Environment.ProcessorCount;
        private readonly int parallelismThreshold = 256; // number takens from a dictionary parallel insertion benchmarks

        private ConcurrentDictionary<OutPoint, UnspentOutput> unspents;

        public TxOut GetOutputFor(TxIn txIn) => this.unspents.TryGet(txIn.PrevOut)?.Coins?.TxOut;

        public bool HaveInputs(Transaction tx) => 
            (tx.Inputs.Count > this.parallelismThreshold ?
             tx.Inputs.AsParallel().WithDegreeOfParallelism(this.degreeOfParallelism) as IEnumerable<TxIn> : 
             tx.Inputs)
                .All(txin => this.GetOutputFor(txin) != null);

        public UnspentOutput AccessCoins(OutPoint outpoint) => this.unspents.TryGet(outpoint);

        public Money GetValueIn(Transaction tx) =>
            (tx.Inputs.Count > this.parallelismThreshold ?
             tx.Inputs.AsParallel().WithDegreeOfParallelism(this.degreeOfParallelism) as IEnumerable<TxIn> : 
             tx.Inputs)
                .Select(txin => this.GetOutputFor(txin).Value).Sum();

        public void Update(Network network, Transaction transaction, int height)
        {
            if (!transaction.IsCoinBase)
            {
                var inputs = transaction.Inputs.Count > this.parallelismThreshold ?
                    transaction.Inputs.AsParallel().WithDegreeOfParallelism(this.degreeOfParallelism) as IEnumerable<TxIn> :
                    transaction.Inputs;

                if (!inputs.All(input => this.AccessCoins(input.PrevOut).Spend()))
                {
                    throw new InvalidOperationException("Unspendable coins are invalid at this point");
                }
            }

            void processing(IndexedTxOut output)
            {
                var outpoint = output.ToOutPoint();
                var coinbase = transaction.IsCoinBase;
                var coinstake = network.Consensus.IsProofOfStake && transaction.IsCoinStake;
                var time = (transaction is IPosTransactionWithTime posTx) ? posTx.Time : 0;

                var coins = new Coins((uint)height, output.TxOut, coinbase, coinstake, time);
                var unspentOutput = new UnspentOutput(outpoint, coins)
                {
                    CreatedFromBlock = true
                };

                // If the output is an opreturn just ignore it
                if (coins.IsPrunable)
                    return;

                // In cases where an output is spent in the same block,
                // It will already exist as an input in the unspent list.
                this.unspents[outpoint] = unspentOutput;
            }

            if (transaction.Outputs.Count > this.parallelismThreshold) 
            {
                transaction.Outputs.AsIndexedOutputs()
                    .AsParallel()
                    .WithDegreeOfParallelism(this.degreeOfParallelism)
                    .ForAll(processing);
            }
            else
            {
                foreach(var output in transaction.Outputs.AsIndexedOutputs())
                {
                    processing(output);
                }
            }
        }

        public void SetCoins(UnspentOutput[] coins)
        {
            if(this.unspents == default) 
                this.unspents = new ConcurrentDictionary<OutPoint, UnspentOutput>(this.degreeOfParallelism, coins.Length);
            else
                this.unspents.Clear();
            
            coins
                .AsParallel()
                .WithDegreeOfParallelism(this.degreeOfParallelism)
                .ForAll(coin => { if (coin != null) this.unspents[coin.OutPoint] = coin; });
        }

        public void TrySetCoins(UnspentOutput[] coins)
        {
            if(this.unspents == default) 
                this.unspents = new ConcurrentDictionary<OutPoint, UnspentOutput>(this.degreeOfParallelism, coins.Length);
            else
                this.unspents.Clear();
            
            coins
                .AsParallel()
                .WithDegreeOfParallelism(this.degreeOfParallelism)
                .ForAll(coin => { if (coin != null) this.unspents.TryAdd(coin.OutPoint, coin); });
        }

        public ICollection<UnspentOutput> GetCoins() => this.unspents.Values;

        public IList<UnspentOutput> GetCoins(uint256 trxid) => [.. this.unspents.Where(w => w.Key.Hash == trxid).Select(u => u.Value)];
    }
}
