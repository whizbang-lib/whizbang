using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Whizbang.Generators.Ledger;

/// <summary>
/// A flattened former-name → current-name mapping derived from the ledger. Value-equatable (record of two
/// strings) so it flows cleanly through the incremental-generator pipeline. <see cref="FormerClrTypeName"/> is a
/// CLR name a type used to have; <see cref="CurrentClrTypeName"/> is the name it carries now.
/// </summary>
internal sealed record RenameAlias(string FormerClrTypeName, string CurrentClrTypeName);

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
  public List<PinnedTypeLedgerEntry> Types { get; set; } = new();

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
  public static PinnedTypeLedger? TryParse(string? json) {
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
        e.FormerNames ??= new List<string>();
      }
      return ledger;
    } catch (JsonException) {
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
  };

  /// <summary>Serializes the ledger to committable JSON (camelCase, indented, entries sorted by CLR name for stable diffs).</summary>
  public string ToJson() {
    var stable = new PinnedTypeLedger {
      Version = Version,
      Types = Types.OrderBy(e => e.ClrTypeName, System.StringComparer.Ordinal).ToList(),
    };
    return JsonSerializer.Serialize(stable, _writeOptions);
  }

  /// <summary>
  /// Merges the committed ledger with the currently-compiled pinned types — a monotonic lockfile update.
  /// EXISTING entries (matched by pinned id) are preserved verbatim: their <c>clrTypeName</c> is NOT refreshed to the
  /// current name, so a rename still surfaces as WHIZ120 until acknowledged, and recorded <c>formerNames</c> are kept.
  /// NEW pinned ids are added with the current name and empty <c>formerNames</c>. Orphan entries (no current type) are
  /// retained (WHIZ121 surfaces them for the developer to prune). The result is deterministic.
  /// </summary>
  public static PinnedTypeLedger Merge(PinnedTypeLedger? existing, IEnumerable<DiscoveredPinnedType> current) {
    var merged = new PinnedTypeLedger { Version = existing?.Version ?? 1 };
    var byPinnedId = new Dictionary<string, PinnedTypeLedgerEntry>(System.StringComparer.OrdinalIgnoreCase);

    if (existing is not null) {
      foreach (var e in existing.Types) {
        byPinnedId[e.PinnedId] = e; // preserve committed entry as-is
      }
    }

    foreach (var type in current) {
      if (byPinnedId.ContainsKey(type.PinnedId)) {
        continue; // existing entry wins — never overwrite a committed name/history
      }
      byPinnedId[type.PinnedId] = new PinnedTypeLedgerEntry {
        PinnedId = type.PinnedId,
        ClrTypeName = type.ClrTypeName,
        Kind = type.Kind,
        FormerNames = new List<string>(),
      };
    }

    merged.Types = byPinnedId.Values
      .OrderBy(e => e.ClrTypeName, System.StringComparer.Ordinal)
      .ToList();
    return merged;
  }
}

/// <summary>One tracked pinned type: its stable id, current CLR name, kind, and the set of names it has previously had.</summary>
internal sealed class PinnedTypeLedgerEntry {
  public string PinnedId { get; set; } = "";
  public string ClrTypeName { get; set; } = "";
  public string? Kind { get; set; }
  public List<string> FormerNames { get; set; } = new();

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
