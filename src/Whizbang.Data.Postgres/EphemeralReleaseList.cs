// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The durable types released for one ephemeral type (<see cref="EphemeralOptions.ReleaseBlockers"/>), as the stored
/// names the history walk compares, and how many entries the configured list already holds.
/// </summary>
/// <param name="Names">The released types' stored names, deduplicated and in ordinal order; never one of the ephemeral
/// type's own names.</param>
/// <param name="ConfiguredCount">Entries in the configured list for the type: the index the report's setting line takes.</param>
/// <docs>fundamentals/events/ephemeral-events#releasing-a-blocking-type</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Release_NamesWithOrWithoutTheAssembly_MatchTheConfiguredTypesAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Release_ForAnotherType_OrOfTheTypeItself_ReleasesNothingAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Report_TheReleaseLine_TakesTheNextFreeIndex_OfAnExistingListAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.FormerNames.cs:Release_AReleasedTypesBareFormerName_ReachesTheEventsStoredUnderItAsync</tests>
internal sealed record EphemeralReleaseList(string[] Names, int ConfiguredCount) {
  /// <summary>Resolves the configured release list of an ephemeral type.</summary>
  /// <param name="releaseBlockers">The configured lists, keyed by ephemeral type name with or without its assembly.</param>
  /// <param name="ephemeral">The ephemeral type's catalog entry.</param>
  /// <param name="catalog">Every message type the application declares: a value without its assembly is resolved
  /// through it to the type's stored name (<see cref="TypeNameFormatter.Format(Type)"/>), with the type's former names in
  /// their stored form (<see cref="TypeNameFormatter.FormatFormerName"/>, #2269).</param>
  /// <returns>The release list; empty when none is configured.</returns>
  public static EphemeralReleaseList Resolve(
      IReadOnlyDictionary<string, List<string>> releaseBlockers, MessageTypeCatalogEntry ephemeral,
      IReadOnlyList<MessageTypeCatalogEntry> catalog) {
    string[] own = [ephemeral.ClrTypeName, .. ephemeral.FormerNames];
    var ownKeys = own.Select(TypeNameFormatter.GetFullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var names = new SortedSet<string>(StringComparer.Ordinal);
    var configured = 0;
    foreach (var (key, values) in releaseBlockers) {
      if (!ownKeys.Contains(TypeNameFormatter.GetFullName(key.Trim()))) {
        continue;
      }
      configured += values.Count;
      foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim())) {
        _ = names.Add(EventTypeMatchingHelper.NormalizeTypeName(value));
        var fullName = TypeNameFormatter.GetFullName(value);
        // A catalog name may lack the assembly; the store's name is the type's formatted name, so a match is resolved to that.
        // #2269: a former name is resolved to its stored form too: a bare one takes the type's own assembly.
        foreach (var entry in catalog.Where(e => string.Equals(TypeNameFormatter.GetFullName(e.ClrTypeName), fullName, StringComparison.Ordinal))) {
          names.UnionWith([
            TypeNameFormatter.Format(entry.Type),
            .. entry.FormerNames.Select(n => TypeNameFormatter.FormatFormerName(entry.Type, n)),
          ]);
        }
      }
    }
    names.ExceptWith(own);
    return new EphemeralReleaseList([.. names], configured);
  }
}
