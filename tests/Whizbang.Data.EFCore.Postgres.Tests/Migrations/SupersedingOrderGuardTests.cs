// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// #2350: which copy of a superseding key is newer has one answer, <c>_wh_supersedes</c>: the origin's commit sequence for
/// copies received from the same origin, the stored version otherwise. A function that orders copies of a key by
/// comparing their versions itself makes a restored older copy, stored above the newest, read as the newest; the queue,
/// the reaper and the history walk all did. This guard fails on such a comparison in any function that reads
/// <c>supersede_key</c>, by shape.
/// </summary>
/// <docs>fundamentals/events/superseding-events#redelivered-copies</docs>
[Category("Shard3")]
public partial class SupersedingOrderGuardTests {
  [GeneratedRegex(@"CREATE\s+OR\s+REPLACE\s+FUNCTION\s+__SCHEMA__\.(?<name>[a-z_0-9]+)\s*\(", RegexOptions.IgnoreCase)]
  private static partial Regex CreateFunction();

  [GeneratedRegex(@"drop_all_overloads\s*\(\s*'(?<name>[a-z_0-9]+)'\s*\)", RegexOptions.IgnoreCase)]
  private static partial Regex DropAllOverloads();

  [GeneratedRegex(@"\$(?<tag>[a-z_]*)\$", RegexOptions.IgnoreCase)]
  private static partial Regex DollarQuote();

  [GeneratedRegex(@"--[^\n]*")]
  private static partial Regex LineComment();

  // Two rows' versions compared: the order of two copies decided by where this store put them.
  [GeneratedRegex(@"\b[a-z_]+\.version\s*[<>]=?\s*[a-z_]+\.version\b", RegexOptions.IgnoreCase)]
  private static partial Regex VersionOrder();

  [Test]
  public async Task NoFunctionOrdersTheCopiesOfASupersedingKey_ByTheirStoredVersionAsync() {
    var latest = _latestFunctionBodies(Path.Combine(_repositoryRoot(), "src", "Whizbang.Data.Postgres", "Migrations"));
    var keyed = latest.Where(f => f.Value.Contains("supersede_key", StringComparison.Ordinal)).ToList();
    await Assert.That(keyed.Select(f => f.Key)).Contains("wh_reap_superseded_events")
      .Because("precondition: the scan reads the functions that order the copies of a key");

    var offenders = keyed.Where(f => VersionOrder().IsMatch(f.Value)).Select(f => f.Key).Order(StringComparer.Ordinal).ToList();

    await Assert.That(offenders).IsEmpty()
      .Because("these functions decide which copy of a key is newer by comparing stored versions instead of calling "
        + "_wh_supersedes, so a restored older copy reads as the newest there: " + string.Join(", ", offenders));
  }

  private static Dictionary<string, string> _latestFunctionBodies(string directory) {
    var latest = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var file in Directory.EnumerateFiles(directory, "*.sql").Where(f => !f.EndsWith(".down.sql", StringComparison.Ordinal))
               .Order(StringComparer.Ordinal)) {
      var text = File.ReadAllText(file);
      var events = new List<(int At, string Name, string? Body)>();
      foreach (Match create in CreateFunction().Matches(text)) {
        var open = DollarQuote().Match(text, create.Index);
        var close = text.IndexOf(open.Value, open.Index + open.Length, StringComparison.Ordinal);
        events.Add((create.Index, create.Groups["name"].Value,
          LineComment().Replace(text[(open.Index + open.Length)..close], string.Empty)));
      }
      foreach (Match drop in DropAllOverloads().Matches(text)) {
        events.Add((drop.Index, drop.Groups["name"].Value, null));
      }
      foreach (var (_, name, body) in events.OrderBy(e => e.At)) {
        if (body is null) {
          latest.Remove(name);
        } else {
          latest[name] = body;
        }
      }
    }
    return latest;
  }

  private static string _repositoryRoot() {
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null
        && !(Directory.Exists(Path.Combine(dir.FullName, "src")) && Directory.Exists(Path.Combine(dir.FullName, "tests")))) {
      dir = dir.Parent;
    }
    return dir?.FullName ?? throw new InvalidOperationException("could not find the repository root");
  }
}
