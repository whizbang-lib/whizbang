// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whizbang.Generators.Ledger;

/// <summary>
/// A flattened former-name → current-name mapping derived from the ledger. Value-equatable (record of two
/// strings) so it flows cleanly through the incremental-generator pipeline. <see cref="FormerClrTypeName"/> is a
/// CLR name a type used to have; <see cref="CurrentClrTypeName"/> is the name it carries now.
/// </summary>
internal sealed record RenameAlias(string FormerClrTypeName, string CurrentClrTypeName);

/// <summary>
/// A flattened (pinned id → one former name) pair. Value-equatable so it flows cleanly through the incremental
/// pipeline. Used by <c>MessageTypeCatalogGenerator</c> to attach former names to catalog entries by pinned id.
/// </summary>
internal sealed record PinnedFormerName(string PinnedId, string FormerClrTypeName);

/// <summary>
/// A currently-compiled <c>[PinnedId]</c> type discovered by the ledger generator. Value-equatable so it flows
/// through the incremental pipeline. <see cref="Kind"/> is <c>event</c>/<c>command</c>/<c>message</c>/<c>perspective</c>.
/// </summary>
internal sealed record DiscoveredPinnedType(string PinnedId, string ClrTypeName, string Kind);

/// <summary>
/// The committed pinned-type ledger — a lockfile for type identity. Keyed by <see cref="PinnedTypeLedgerEntry.PinnedId"/>,
/// it records each pinned type's current CLR name plus every FORMER name it has had. The generators and analyzers
/// consume it via <c>AdditionalFiles</c> (<c>.whizbang/pinned-type-ledger.json</c>); the VSCode extension reads the
/// same file to surface identity + rename history and to flag un-acknowledged renames inline.
/// </summary>
/// <remarks>
/// Why a ledger: a pinned type's identity is its PinnedId; its CLR name is a versioned label. Events are written into
/// an append-only log with the name-of-the-day, so a rename must record the old name as an alias — otherwise old events
/// (immutable) can no longer be resolved to the current type. The ledger is that record, reviewed as a lockfile diff.
/// </remarks>
internal sealed class PinnedTypeLedger {
  public int Version { get; set; } = 1;
  public List<PinnedTypeLedgerEntry> Types { get; set; } = [];

  /// <summary>Standard on-disk file name. Discovered among <c>AdditionalFiles</c> by suffix match.</summary>
  public const string FILE_NAME = "pinned-type-ledger.json";

  /// <summary>True when <paramref name="path"/> points at a <c>.whizbang/pinned-type-ledger.json</c> (separator-agnostic).</summary>
  public static bool IsLedgerPath(string path) {
    var normalized = path.Replace('\\', '/');
    return normalized.EndsWith("/" + FILE_NAME, System.StringComparison.OrdinalIgnoreCase) ||
           System.IO.Path.GetFileName(path).Equals(FILE_NAME, System.StringComparison.OrdinalIgnoreCase);
  }

  private static readonly JsonSerializerOptions _readOptions = new() {
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
  };

  /// <summary>
  /// Parses ledger JSON. Returns null when <paramref name="json"/> is null/blank or malformed — callers treat a missing
  /// or unparseable ledger as "no baseline" (the governance analyzer stays inert rather than failing the build on a
  /// broken ledger). Entries with a blank PinnedId or ClrTypeName are dropped.
  /// </summary>
  public static PinnedTypeLedger? TryParse(string? json) => TryParse(json, out _);

  /// <summary>
  /// Parses ledger JSON, additionally reporting whether the input was <paramref name="malformed"/> — i.e.
  /// present but unparseable — so callers can distinguish "no ledger" (inert, fine) from "broken ledger"
  /// (worth surfacing). <paramref name="malformed"/> is <c>false</c> for null/blank input (an absent ledger)
  /// and <c>true</c> only when non-blank input fails to parse as JSON. Return value semantics are identical
  /// to <see cref="TryParse(string?)"/>: null on absent or malformed, the ledger otherwise.
  /// </summary>
  public static PinnedTypeLedger? TryParse(string? json, out bool malformed) {
    malformed = false;
    if (string.IsNullOrWhiteSpace(json)) {
      return null;
    }
    try {
      var ledger = JsonSerializer.Deserialize<PinnedTypeLedger>(json!, _readOptions);
      if (ledger?.Types is null) {
        return null;
      }
      ledger.Types.RemoveAll(e => e is null
        || string.IsNullOrWhiteSpace(e.PinnedId)
        || string.IsNullOrWhiteSpace(e.ClrTypeName));
      foreach (var e in ledger.Types) {
        e.FormerNames ??= [];
      }
      return ledger;
    } catch (JsonException) {
      malformed = true;
      return null;
    }
  }

  /// <summary>
  /// Flattens the ledger into one <see cref="RenameAlias"/> per (former name → current name) pair. Skips blank
  /// former names and any former name equal to the entry's own current name (a no-op alias).
  /// </summary>
  public IEnumerable<RenameAlias> ToRenameAliases() {
    foreach (var entry in Types) {
      foreach (var former in entry.FormerNames) {
        if (string.IsNullOrWhiteSpace(former) ||
            string.Equals(former, entry.ClrTypeName, System.StringComparison.Ordinal)) {
          continue;
        }
        yield return new RenameAlias(former, entry.ClrTypeName);
      }
    }
  }

  /// <summary>
  /// Flattens the ledger into one <see cref="PinnedFormerName"/> per (pinned id → former name) pair. Skips entries
  /// with a blank pinned id, and blank former names or a former name equal to the entry's own current name.
  /// </summary>
  public IEnumerable<PinnedFormerName> ToPinnedFormerNames() {
    foreach (var entry in Types) {
      if (string.IsNullOrWhiteSpace(entry.PinnedId)) {
        continue;
      }
      foreach (var former in entry.FormerNames) {
        if (string.IsNullOrWhiteSpace(former) ||
            string.Equals(former, entry.ClrTypeName, System.StringComparison.Ordinal)) {
          continue;
        }
        yield return new PinnedFormerName(entry.PinnedId, former);
      }
    }
  }

  /// <summary>
  /// The assembly a former name records (<c>Ns.Type, Assembly</c>, #2264), or null for a bare name. The assembly follows
  /// the first comma outside a generic argument list, so a bare generic name (<c>Ns.G`1[[A, B]]</c>) records none.
  /// </summary>
  /// <param name="formerName">A former name, bare or with its assembly.</param>
  /// <returns>The recorded assembly, or null.</returns>
  /// <tests>tests/Whizbang.Generators.Tests/PinnedTypeLedgerAssemblyMoveTests.cs:AnEntryThatMovedHere_RecordsEveryNameItWasStoredUnder_WithTheAssemblyItWasStoredInAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PinnedTypeLedgerAssemblyMoveTests.cs:ABareGenericFormerName_RecordsNoAssemblyAsync</tests>
  public static string? AssemblyOf(string formerName) {
    var depth = 0;
    for (var i = 0; i < formerName.Length; i++) {
      switch (formerName[i]) {
        case '[':
          depth++;
          break;
        case ']':
          depth--;
          break;
        case ',' when depth == 0:
          return formerName.Substring(i + 1).Trim();
      }
    }
    return null;
  }

  /// <summary>Finds an entry by pinned id (ordinal, case-insensitive on the guid text). Null if absent.</summary>
  public PinnedTypeLedgerEntry? FindByPinnedId(string pinnedId) {
    foreach (var e in Types) {
      if (string.Equals(e.PinnedId, pinnedId, System.StringComparison.OrdinalIgnoreCase)) {
        return e;
      }
    }
    return null;
  }

  private static readonly JsonSerializerOptions _writeOptions = new() {
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    // The ledger is a file-destined, human-reviewed lockfile — never embedded in HTML — so use the relaxed
    // encoder. The default HTML-safe encoder escapes '+' (and angle brackets and ampersand) to a 6-char unicode
    // escape, which would render every nested-type name's '+' separator as an unreadable escape and wreck diffs.
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    // Indented output breaks lines with Environment.NewLine unless told otherwise, which made a Windows build's
    // ledger CRLF and a Linux build's LF. A committed lockfile must be the same bytes on every build machine.
    NewLine = "\n",
  };

  /// <summary>Serializes the ledger to committable JSON (camelCase, indented, entries sorted by CLR name for stable diffs).</summary>
  public string ToJson() {
    var stable = new PinnedTypeLedger {
      Version = Version,
      Types = [.. Types.OrderBy(e => e.ClrTypeName, System.StringComparer.Ordinal)],
    };
    return JsonSerializer.Serialize(stable, _writeOptions);
  }

  /// <summary>
  /// Merges the committed ledger with the currently-compiled pinned types, compiled in <paramref name="assemblyName"/>
  /// (null when the compilation has none): a monotonic lockfile update.
  /// EXISTING entries (matched by pinned id) keep their <c>clrTypeName</c>, which is NOT refreshed to the current name, so
  /// a rename still surfaces as WHIZ120 until acknowledged, and recorded <c>formerNames</c> are kept. An existing entry is
  /// placed in this assembly (<see cref="PinnedTypeLedgerEntry.PlacedIn"/>), which records the names it was stored under
  /// in another assembly when it moved here (#2264).
  /// NEW pinned ids are added with the current name, this assembly and empty <c>formerNames</c>. Orphan entries (no
  /// current type) are retained (WHIZ121 surfaces them for the developer to prune). The result is deterministic.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/PinnedTypeLedgerAssemblyMoveTests.cs</tests>
  public static PinnedTypeLedger Merge(PinnedTypeLedger? existing, IEnumerable<DiscoveredPinnedType> current, string? assemblyName) {
    var merged = new PinnedTypeLedger { Version = existing?.Version ?? 1 };
    var byPinnedId = new Dictionary<string, PinnedTypeLedgerEntry>(System.StringComparer.OrdinalIgnoreCase);

    if (existing is not null) {
      foreach (var e in existing.Types) {
        byPinnedId[e.PinnedId] = e; // preserve committed entry as-is
      }
    }

    foreach (var type in current) {
      if (byPinnedId.TryGetValue(type.PinnedId, out var committed)) {
        // The existing entry wins (never overwrite a committed name or history); it is only placed in this assembly.
        byPinnedId[type.PinnedId] = committed.PlacedIn(assemblyName);
        continue;
      }
      byPinnedId[type.PinnedId] = new PinnedTypeLedgerEntry {
        PinnedId = type.PinnedId,
        ClrTypeName = type.ClrTypeName,
        Kind = type.Kind,
        Assembly = assemblyName,
        FormerNames = [],
      };
    }

    merged.Types = [.. byPinnedId.Values.OrderBy(e => e.ClrTypeName, System.StringComparer.Ordinal)];
    return merged;
  }
}

/// <summary>One tracked pinned type: its stable id, current CLR name, kind, and the set of names it has previously had.</summary>
internal sealed class PinnedTypeLedgerEntry {
  public string PinnedId { get; set; } = "";
  public string ClrTypeName { get; set; } = "";
  public string? Kind { get; set; }

  /// <summary>
  /// The assembly the type is compiled in, written by the merge; null on an entry written before assemblies were
  /// recorded, which is taken to be in the assembly that builds it.
  /// </summary>
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public string? Assembly { get; set; }

  /// <summary>
  /// The names the type was formerly known by. A bare name (<c>Ns.Type</c>) was the type's name in the assembly it is
  /// compiled in now; a name with an assembly (<c>Ns.Type, Assembly</c>) was its name in that assembly (#2264).
  /// </summary>
  public List<string> FormerNames { get; set; } = [];

  /// <summary>
  /// This entry as compiled in <paramref name="assemblyName"/>. An entry with no recorded assembly is taken to be there
  /// already. An entry recorded in another assembly moved here with its type: the names it was stored under there (its
  /// recorded name and each bare former name) become former names with that assembly, and a former name that already
  /// names an assembly keeps it. With no assembly name there is nothing to compare, so the entry is left as it is.
  /// </summary>
  /// <param name="assemblyName">The assembly the type is compiled in now, or null when the compilation has none.</param>
  /// <returns>The entry, placed in <paramref name="assemblyName"/>.</returns>
  /// <tests>tests/Whizbang.Generators.Tests/PinnedTypeLedgerAssemblyMoveTests.cs:AnEntryThatMovedHere_RecordsEveryNameItWasStoredUnder_WithTheAssemblyItWasStoredInAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PinnedTypeLedgerAssemblyMoveTests.cs:AnEntryThatMovedHere_UnderANameItAlreadyRecordedWithTheOldAssembly_RecordsItOnceAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PinnedTypeLedgerAssemblyMoveTests.cs:ARenameWithinTheAssembly_KeepsItsFormerNamesBareAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PinnedTypeLedgerAssemblyMoveTests.cs:AnEntryWrittenBeforeAssembliesWereRecorded_IsTakenToBeInThisAssembly_AndKeepsItsFormerNamesBareAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PinnedTypeLedgerAssemblyMoveTests.cs:AnUnnamedAssembly_RecordsNoAssembly_AndLeavesAnExistingEntryAsItWasAsync</tests>
  public PinnedTypeLedgerEntry PlacedIn(string? assemblyName) {
    if (assemblyName is null || string.Equals(Assembly, assemblyName, System.StringComparison.Ordinal)) {
      return this;
    }
    var formerNames = FormerNames;
    if (!string.IsNullOrWhiteSpace(Assembly)) {
      formerNames = [];
      foreach (var name in FormerNames.Append(ClrTypeName)) {
        var stored = PinnedTypeLedger.AssemblyOf(name) is null ? $"{name}, {Assembly}" : name;
        if (!formerNames.Contains(stored)) {
          formerNames.Add(stored);
        }
      }
    }
    return new PinnedTypeLedgerEntry {
      PinnedId = PinnedId,
      ClrTypeName = ClrTypeName,
      Kind = Kind,
      Assembly = assemblyName,
      FormerNames = formerNames,
    };
  }

  /// <summary>True when <paramref name="name"/> is the current name or any recorded former name (ordinal).</summary>
  public bool KnowsName(string name) {
    if (string.Equals(ClrTypeName, name, System.StringComparison.Ordinal)) {
      return true;
    }
    foreach (var f in FormerNames) {
      if (string.Equals(f, name, System.StringComparison.Ordinal)) {
        return true;
      }
    }
    return false;
  }
}
