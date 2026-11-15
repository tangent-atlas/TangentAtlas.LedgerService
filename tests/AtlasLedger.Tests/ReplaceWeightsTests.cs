// <copyright file="ReplaceWeightsTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias client;

namespace AtlasLedger.Tests
{
    using System;
    using System.Linq;
    using client::AtlasLedger.Client;
    using client::AtlasLedger.Client.Scripts;
    using client::AtlasLedger.Model;
    using Xunit;

    /// <summary>
    /// Replacing a model's weights: the batch changes, removes and adds rows to match, and leaves other models alone.
    /// </summary>
    public sealed class ReplaceWeightsTests
    {
        [Fact]
        public void TheBatchTurnsTheCurrentWeightsIntoTheWantedOnes()
        {
            var (joe10, other) = (Guid.NewGuid(), Guid.NewGuid());
            var (kept, changed, dropped, added) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            var current = new[]
            {
                new WeightDto { ModelId = joe10, AssetId = kept, Fraction = 0.1m, RowVersion = 11 },
                new WeightDto { ModelId = joe10, AssetId = changed, Fraction = 0.2m, RowVersion = 12 },
                new WeightDto { ModelId = joe10, AssetId = dropped, Fraction = 0.3m, RowVersion = 13 },
                new WeightDto { ModelId = other, AssetId = dropped, Fraction = 0.9m, RowVersion = 14 },
            };
            var wanted = new[]
            {
                new WeightDto { ModelId = joe10, AssetId = kept, Fraction = 0.1m },
                new WeightDto { ModelId = joe10, AssetId = changed, Fraction = 0.25m },
                new WeightDto { ModelId = joe10, AssetId = added, Fraction = 0.15m },
            };

            var batch = Commands.ReplaceWeights(wanted, current);

            Assert.Equal(
                [(ScriptAction.Insert, 0L), (ScriptAction.Update, 0L), (ScriptAction.Delete, 13L)],
                batch.Operations.Select(operation => (operation.Action, operation.RowVersion)).OrderBy(item => item.Action));
            Assert.Contains("12", batch.Operations.Single(operation => operation.Action == ScriptAction.Update).Row!.Value.GetRawText(), StringComparison.Ordinal);   // updated at its current version
            Assert.Empty(Commands.ReplaceWeights(current.Take(3).ToArray(), current).Operations);   // already as wanted: nothing to do
        }
    }
}
