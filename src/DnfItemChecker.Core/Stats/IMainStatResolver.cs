using DnfItemChecker.Core.Models;

namespace DnfItemChecker.Core.Stats;

/// <summary>
/// Resolves a character's primary stat (힘/지능/체력/정신력).
/// Buffers scale with 체력/정신력; physical dealers with 힘; magical dealers with 지능.
/// </summary>
public interface IMainStatResolver
{
    /// <summary>
    /// Primary stat for the given advancement (전직). <paramref name="jobGrowId"/> is
    /// preferred; <paramref name="jobGrowName"/> is a fallback. Null when unknown.
    /// </summary>
    MainStat? Resolve(string? jobGrowId, string? jobGrowName);

    /// <summary>
    /// Primary stat inferred from a character's final status block (argmax over the four
    /// primary stats). The most reliable path when /status is available. Null if absent.
    /// </summary>
    MainStat? ResolveFromStatus(IReadOnlyList<DfStat> status);
}
