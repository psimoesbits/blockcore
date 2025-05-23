﻿using System.Collections.Concurrent;
using System.Collections.Generic;
using Blockcore.Consensus.TransactionInfo;
using Blockcore.Utilities;

namespace Blockcore.Features.Consensus.CoinViews
{
    /// <summary>
    /// Return value of <see cref="CoinView.FetchCoinsAsync(OutPoint[])"/>,
    /// contains the coinview tip's hash and information about unspent coins in the requested transactions.
    /// </summary>
    public class FetchCoinsResponse
    {
        /// <summary>Unspent outputs of the requested transactions.</summary>
        public IDictionary<OutPoint, UnspentOutput> UnspentOutputs { get; private set; }

        public FetchCoinsResponse()
        {
            this.UnspentOutputs = new ConcurrentDictionary<OutPoint, UnspentOutput>();
        }
    }
}
