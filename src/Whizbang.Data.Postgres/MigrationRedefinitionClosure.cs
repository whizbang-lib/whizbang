using System;
using System.Collections.Generic;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Expands the ledger's re-run set to its redefinition closure: whenever a migration re-runs, every
/// LATER migration that defines any of the same SQL objects re-runs after it, so the database always
/// ends on each object's last-word definition. Without this, a hash-driven replay of an earlier
/// file silently leaves objects generations old — every statement succeeds while, e.g., store
/// procedures persist <c>flags = 0</c> for every row with no error anywhere.
/// </summary>
/// <docs>operations/migrations</docs>
public static class MigrationRedefinitionClosure {
  /// <summary>
  /// Returns the fixed-point closure of <paramref name="toRun"/> over
  /// <paramref name="orderedMigrations"/> (ledger order): for every migration in the set, every
  /// LATER migration sharing any object is added, repeated until stable — a pulled-in file's OWN
  /// objects propagate too. The result always contains the (known) members of
  /// <paramref name="toRun"/> itself.
  /// </summary>
  /// <param name="orderedMigrations">All migrations in ledger (numeric) order with their object lists.</param>
  /// <param name="toRun">Names the ledger already decided to execute (new or hash-drifted).</param>
  public static IReadOnlySet<string> Expand(
      IReadOnlyList<(string Name, IReadOnlyCollection<string> Objects)> orderedMigrations,
      IReadOnlyCollection<string> toRun) {
    ArgumentNullException.ThrowIfNull(orderedMigrations);
    ArgumentNullException.ThrowIfNull(toRun);

    var result = new HashSet<string>(toRun, StringComparer.Ordinal);
    if (result.Count == 0) {
      return result;
    }

    bool changed;
    do {
      changed = false;

      // Earliest in-closure index defining each object — anything LATER that defines the same
      // object must join the closure so the last word runs last.
      var earliestByObject = new Dictionary<string, int>(StringComparer.Ordinal);
      for (var i = 0; i < orderedMigrations.Count; i++) {
        if (!result.Contains(orderedMigrations[i].Name)) {
          continue;
        }
        foreach (var obj in orderedMigrations[i].Objects) {
          if (!earliestByObject.TryGetValue(obj, out var existing) || i < existing) {
            earliestByObject[obj] = i;
          }
        }
      }

      for (var i = 0; i < orderedMigrations.Count; i++) {
        if (result.Contains(orderedMigrations[i].Name)) {
          continue;
        }
        foreach (var obj in orderedMigrations[i].Objects) {
          if (earliestByObject.TryGetValue(obj, out var earliest) && i > earliest) {
            result.Add(orderedMigrations[i].Name);
            changed = true;
            break;
          }
        }
      }
    } while (changed);

    return result;
  }
}
