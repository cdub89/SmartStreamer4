using System;
using System.Collections.Generic;
using System.Linq;

namespace SDRIQStreamer.FlexRadio;

/// <summary>
/// Inference layer over panadapter state for "which stations are using this
/// DAX-IQ channel." DAX-the-app's actual bind state isn't exposed via FlexLib,
/// so multi-station channel assignments are the strongest signal we have for
/// the wrong-station-routing ambiguity (issue #39).
/// </summary>
public static class DaxChannelOwnership
{
    /// <summary>
    /// Distinct, case-insensitive, alphabetically ordered list of stations
    /// whose panadapters have <paramref name="daxIqChannel"/> assigned.
    /// Returns empty when no station has the channel.
    /// </summary>
    public static IReadOnlyList<string> GetOwners(
        IReadOnlyList<PanadapterInfo> panadapters,
        int daxIqChannel)
    {
        ArgumentNullException.ThrowIfNull(panadapters);

        if (daxIqChannel <= 0)
            return [];

        return panadapters
            .Where(p => p.DAXIQChannel == daxIqChannel && !string.IsNullOrWhiteSpace(p.ClientStation))
            .Select(p => p.ClientStation)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Returns the foreign stations holding <paramref name="daxIqChannel"/>
    /// (every owner except <paramref name="ownStation"/>). Empty when only
    /// our station holds the channel or no station does.
    /// </summary>
    public static IReadOnlyList<string> GetForeignOwners(
        IReadOnlyList<PanadapterInfo> panadapters,
        int daxIqChannel,
        string ownStation)
    {
        ArgumentNullException.ThrowIfNull(panadapters);
        if (string.IsNullOrWhiteSpace(ownStation))
            return GetOwners(panadapters, daxIqChannel);

        return GetOwners(panadapters, daxIqChannel)
            .Where(s => !string.Equals(s, ownStation, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// True when a pan/stream update identified by
    /// <paramref name="sourceClientHandle"/> on <paramref name="daxIqChannel"/>
    /// originates from a panadapter owned by <paramref name="ownStation"/>.
    /// Used as the LO-sync chokepoint gate so foreign-station updates on a
    /// shared DAX-IQ channel cannot push their center as our LO.
    /// </summary>
    public static bool IsSourceOwnedByStation(
        IReadOnlyList<PanadapterInfo> panadapters,
        int daxIqChannel,
        uint sourceClientHandle,
        string ownStation)
    {
        ArgumentNullException.ThrowIfNull(panadapters);
        if (daxIqChannel <= 0 || sourceClientHandle == 0 ||
            string.IsNullOrWhiteSpace(ownStation))
        {
            return false;
        }

        return panadapters.Any(p =>
            p.DAXIQChannel == daxIqChannel &&
            p.ClientHandle == sourceClientHandle &&
            string.Equals(p.ClientStation, ownStation, StringComparison.OrdinalIgnoreCase));
    }
}
