using SCStreamDeck.Common;
using SCStreamDeck.Models;

namespace SCStreamDeck.Services.Core;

internal static class InstallationDetectionMerger
{
    private static string BuildCandidateKey(SCInstallCandidate candidate) => $"{candidate.RootPath}|{candidate.Channel}";

    public static (Dictionary<string, SCInstallCandidate> CandidateMap, Dictionary<string, string> DetectionSources) Merge(
        IReadOnlyList<SCInstallCandidate> rsiLogCandidates,
        IReadOnlyList<SCInstallCandidate> validCachedCandidates,
        bool needsFullDetection)
    {
        ArgumentNullException.ThrowIfNull(rsiLogCandidates);
        ArgumentNullException.ThrowIfNull(validCachedCandidates);

        Dictionary<string, SCInstallCandidate> candidateMap = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> candidateSources = new(StringComparer.OrdinalIgnoreCase);

        foreach (SCInstallCandidate candidate in rsiLogCandidates)
        {
            string key = BuildCandidateKey(candidate);
            candidateMap[key] = candidate;
            candidateSources[key] = "RSI Logs";
        }

        // Always merge cached candidates directly (preserves custom overrides).
        foreach (SCInstallCandidate cached in validCachedCandidates)
        {
            string key = BuildCandidateKey(cached);

            if (cached.Source == InstallSource.UserProvided)
            {
                // Custom overrides always win over any auto-detected candidate.
                candidateMap[key] = cached;
                candidateSources[key] = "Custom Override";
                continue;
            }

            if (candidateMap.TryAdd(key, cached))
            {
                candidateSources[key] = "Cache";
            }
        }

        if (!needsFullDetection && validCachedCandidates.Count > 0)
        {
            MergeNewChannelsFromCachedRoots(validCachedCandidates, candidateMap, candidateSources);
        }

        return KeepOneCandidatePerChannel(candidateMap, candidateSources);
    }

    private static void MergeNewChannelsFromCachedRoots(
        IReadOnlyList<SCInstallCandidate> validCachedCandidates,
        Dictionary<string, SCInstallCandidate> candidateMap,
        Dictionary<string, string> candidateSources)
    {
        HashSet<string> cachedRootPaths = validCachedCandidates
            .Select(c => c.RootPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        HashSet<SCChannel> cachedChannels = validCachedCandidates
            .Select(c => c.Channel)
            .ToHashSet();

        List<SCInstallCandidate> cachedPathCandidates = [];
        foreach (string rootPath in cachedRootPaths)
        {
            InstallationCandidateEnumerator.AddCandidatesFromRoot(cachedPathCandidates, rootPath);
        }

        foreach (SCInstallCandidate candidate in cachedPathCandidates)
        {
            string key = BuildCandidateKey(candidate);

            if (candidateMap.TryAdd(key, candidate))
            {
                candidateSources[key] = "Cache (New Channel)";
            }
            else if (cachedChannels.Contains(candidate.Channel))
            {
                candidateSources[key] = "Cache";
            }
        }
    }

    /// <summary>
    ///     Several roots can report the same channel (e.g. an old install left on another drive).
    ///     State, keybinding generation and actionmaps watchers expect one install per channel, so keep
    ///     the custom override if there is one, otherwise the install with the newest Data.p4k.
    ///     Ties keep detection order.
    /// </summary>
    private static (Dictionary<string, SCInstallCandidate> CandidateMap, Dictionary<string, string> DetectionSources)
        KeepOneCandidatePerChannel(
            Dictionary<string, SCInstallCandidate> candidateMap,
            Dictionary<string, string> candidateSources)
    {
        Dictionary<string, SCInstallCandidate> selected = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> detectionSources = [];

        IEnumerable<KeyValuePair<string, SCInstallCandidate>> bestPerChannel = candidateMap
            .GroupBy(entry => entry.Value.Channel)
            .Select(group => group
                .OrderByDescending(entry => entry.Value.Source == InstallSource.UserProvided)
                .ThenByDescending(entry => GetDataP4KLastWriteUtc(entry.Value))
                .First());

        foreach ((string key, SCInstallCandidate candidate) in bestPerChannel)
        {
            selected[key] = candidate;
            detectionSources[candidate.Channel.ToString()] = candidateSources[key];
        }

        return (selected, detectionSources);
    }

    private static DateTime GetDataP4KLastWriteUtc(SCInstallCandidate candidate)
    {
        try
        {
            return File.GetLastWriteTimeUtc(candidate.DataP4KPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return DateTime.MinValue;
        }
    }
}
