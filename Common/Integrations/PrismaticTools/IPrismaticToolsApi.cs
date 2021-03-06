using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Pathoschild.Stardew.Common.Integrations.BetterSprinklers
{
    /// <summary>The API provided by the Prismatic Tools mod.</summary>
    public interface IPrismaticToolsApi
    {
        /// <summary>The prismatic sprinkler object ID.</summary>
        int SprinklerIndex { get; }

        /// <summary>Get the relative tile coverage for a prismatic sprinkler.</summary>
        /// <param name="origin">The sprinkler tile.</param>
        IEnumerable<Vector2> GetSprinklerCoverage(Vector2 origin);
    }
}
