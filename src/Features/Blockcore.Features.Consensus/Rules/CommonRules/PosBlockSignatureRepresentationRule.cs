﻿using Blockcore.Consensus;
using Blockcore.Consensus.BlockInfo;
using Blockcore.Consensus.Rules;

namespace Blockcore.Features.Consensus.Rules.CommonRules
{
    /// <summary>
    /// Check that the block signature for a POS block is in the canonical format.
    /// </summary>
    /// <remarks>This rule can only be an integrity validation rule. If it is
    /// used as a partial or full validation rule, the block itself will get banned
    /// instead of the peer, which can result in a chain split as the C++ node
    /// only bans the peer.</remarks>
    public class PosBlockSignatureRepresentationRule : IntegrityValidationConsensusRule
    {
        /// <inheritdoc />
        /// <exception cref="ConsensusErrors.BadBlockSignature">The block signature is not in the canonical format.</exception>
        public override void Run(RuleContext context)
        {
            if (context.SkipValidation)
                return;

            if (!PosBlockValidator.IsCanonicalBlockSignature((PosBlock)context.ValidationContext.BlockToValidate, true))
            {
                ConsensusErrors.BadBlockSignature.Throw();
            }
        }
    }
}