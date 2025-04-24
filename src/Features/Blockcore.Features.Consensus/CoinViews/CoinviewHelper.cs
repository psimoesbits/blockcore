using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Blockcore.Consensus.BlockInfo;
using Blockcore.Consensus.TransactionInfo;
using Blockcore.NBitcoin;

namespace Blockcore.Features.Consensus.CoinViews
{
    public class CoinviewHelper
    {
        private readonly int degreeOfParallelism = Math.Min(Environment.ProcessorCount, 512);
        private readonly int degreeOfParallelismHalved = Math.Max(1, Math.Min(Environment.ProcessorCount, 512));
        private readonly int parallelismThreshold = 256; // number takens from a dictionary parallel insertion benchmarks

        /// <summary>
        /// Gets transactions identifiers that need to be fetched from store for specified block.
        /// </summary>
        /// <param name="block">The block with the transactions.</param>
        /// <param name="enforceBIP30">Whether to enforce look up of the transaction id itself and not only the reference to previous transaction id.</param>
        /// <returns>A list of transaction ids to fetch from store</returns>
        public OutPoint[] GetIdsToFetch(Block block, bool enforceBIP30)
        {
            var ids = new ConcurrentBag<OutPoint>();
            var trx = new HashSet<uint256>(block.Transactions.Select(tx => tx.GetHash()));

            void processingInput(TxIn input)
            {
                // Check if an output is spend in the same block
                // in case it was ignore it as no need to fetch it from disk.
                // This extra hash list has a small overhead 
                // but it's faster then fetching from disk an empty utxo.
                if (!trx.Contains(input.PrevOut.Hash))
                {
                    ids.Add(input.PrevOut);
                }
            }
            
            void processing(Transaction tx)
            {
                if (enforceBIP30)
                {
                    if(tx.Outputs.Count > this.parallelismThreshold)
                    {
                        tx.Outputs
                            .Select((_, i) => new OutPoint(tx, i))
                            .AsParallel()
                            .WithDegreeOfParallelism(this.degreeOfParallelismHalved)
                            .ForAll(outpoint => ids.Add(outpoint));
                    }
                    else
                    {
                        for(int i = 0; i < tx.Outputs.Count; i++)
                        {
                            ids.Add(new OutPoint(tx, i));
                        }
                    }
                }

                if (!tx.IsCoinBase)
                {
                    if(tx.Inputs.Count > this.parallelismThreshold)
                    {
                        tx.Inputs
                            .AsParallel()
                            .WithDegreeOfParallelism(this.degreeOfParallelismHalved)
                            .ForAll(input => processingInput(input));
                    }
                    else
                    {
                        foreach (TxIn input in tx.Inputs)
                        {
                            processingInput(input);
                        }
                    }
                }
            }

            if (block.Transactions.Count > this.parallelismThreshold)
            {
                block.Transactions
                    .AsParallel()
                    .WithDegreeOfParallelism(this.degreeOfParallelism)
                    .ForAll(tx => processing(tx));
            }
            else
            {
                foreach (Transaction tx in block.Transactions)
                {
                    processing(tx);
                }
            }

            return [.. ids];
        }
    }
}
