// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// #2106: stream digest math has one home. Every function that writes <c>wh_stream_digests</c> folds through
/// <c>_wh_fold_stream_digests</c>, and every digest computed on the fly (in a SQL function or in C# SQL) takes an event's
/// lanes from <c>_wh_digest_lane</c>. A copy of the math anywhere else is how one writer drifts from the others and
/// silently corrupts stored digests, so a copy fails this guard by shape, not by a list of names a new function could
/// be absent from.
/// </summary>
/// <remarks>
/// The scan reads each function's LATEST definition across the migrations (a later <c>CREATE OR REPLACE</c> supersedes
/// an earlier one, and a later <c>drop_all_overloads</c> retires it), with SQL comments removed, and every C# source
/// line under <c>src</c> that is not a comment.
/// </remarks>
/// <docs>resilience/stream-integrity#one-digest-fold</docs>
[Category("Shard3")]
public partial class StreamDigestFoldGuardTests {
  /// <summary>The helpers that own the math: the only bodies allowed to hash an event id or write a digest row.</summary>
  private static readonly HashSet<string> _helpers = new(StringComparer.Ordinal) {
    "_wh_digest_lane", "_wh_fold_stream_digests", "_wh_heal_stream_digests",
  };

  [GeneratedRegex(@"CREATE\s+OR\s+REPLACE\s+FUNCTION\s+__SCHEMA__\.(?<name>[a-z_0-9]+)\s*\(", RegexOptions.IgnoreCase)]
  private static partial Regex CreateFunction();

  [GeneratedRegex(@"drop_all_overloads\s*\(\s*'(?<name>[a-z_0-9]+)'\s*\)", RegexOptions.IgnoreCase)]
  private static partial Regex DropAllOverloads();

  [GeneratedRegex(@"\$(?<tag>[a-z_]*)\$", RegexOptions.IgnoreCase)]
  private static partial Regex DollarQuote();

  // A statement that sets digest lanes. Deleting a stream's buckets whole (a purge, an archive) sets no lane.
  [GeneratedRegex(@"(INSERT\s+INTO|UPDATE)\s+(__SCHEMA__\.)?wh_stream_digests\b", RegexOptions.IgnoreCase)]
  private static partial Regex DigestWrite();

  // An event id hashed into a digest lane. hashtextextended over anything else (an advisory lock key) is not digest math.
  [GeneratedRegex(@"hashtextextended\s*\(\s*([a-z_]+\.)?(event_id|message_id|p_event_id)\b", RegexOptions.IgnoreCase)]
  private static partial Regex DigestHash();

  // The same statement written in C# SQL, where the schema is interpolated.
  [GeneratedRegex(@"(INSERT\s+INTO|UPDATE)\s+(\{schema\}\.)?wh_stream_digests\b", RegexOptions.IgnoreCase)]
  private static partial Regex CSharpDigestWrite();

  [GeneratedRegex(@"--[^\n]*")]
  private static partial Regex LineComment();

  [Test]
  public async Task EveryFunctionThatWritesOrHashesStreamDigests_FoldsThroughTheSharedHelperAsync() {
    var latest = _latestFunctionBodies(_migrationsDirectory());

    await Assert.That(latest.Keys.Count).IsGreaterThan(100)
      .Because("precondition: the scan found the migrations' functions, or it would pass by reading nothing");
    await Assert.That(latest.ContainsKey("_emit_event_store_chain")).IsTrue()
      .Because("precondition: the event write path is among the functions read");

    var offenders = latest
      .Where(f => !_helpers.Contains(f.Key) && (DigestWrite().IsMatch(f.Value) || DigestHash().IsMatch(f.Value)))
      .Select(f => f.Key)
      .Order(StringComparer.Ordinal)
      .ToList();

    await Assert.That(offenders).IsEmpty()
      .Because("these functions write wh_stream_digests or hash an event id themselves instead of calling "
        + "_wh_fold_stream_digests / _wh_digest_lane, so a change to the digest made in one place misses them: "
        + string.Join(", ", offenders));
  }

  [Test]
  public async Task NoCSharpSqlHashesAnEventIdOrWritesDigestLanesItselfAsync() {
    var root = _repositoryRoot();
    var offenders = new SortedSet<string>(StringComparer.Ordinal);
    foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)) {
      if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) {
        continue;
      }
      var lines = await File.ReadAllLinesAsync(file);
      for (var i = 0; i < lines.Length; i++) {
        var trimmed = lines[i].TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*')
            || !(DigestHash().IsMatch(lines[i]) || CSharpDigestWrite().IsMatch(lines[i]))) {
          continue;
        }
        offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}");
      }
    }

    await Assert.That(offenders).IsEmpty()
      .Because("a digest computed on the fly must take an event's lanes from _wh_digest_lane, and a stored digest is "
        + "written only by the SQL helpers, or stored and live digests can disagree: " + string.Join(", ", offenders));
  }

  [Test]
  public async Task TheGuard_FlagsAFunctionThatWritesDigestsItself_AndPassesOneThatCallsTheHelperAsync() {
    var dir = Directory.CreateTempSubdirectory("digest-guard-");
    try {
      await File.WriteAllTextAsync(Path.Combine(dir.FullName, "001_a.sql"), """
        CREATE OR REPLACE FUNCTION __SCHEMA__.copies_the_math() RETURNS void AS $$
        BEGIN
          UPDATE __SCHEMA__.wh_stream_digests SET event_count = event_count + 1; -- a write of its own
        END;
        $$ LANGUAGE plpgsql;
        CREATE OR REPLACE FUNCTION __SCHEMA__.retired() RETURNS void AS $fn$
        BEGIN
          PERFORM hashtextextended(p_event_id::text, 0);
        END;
        $fn$ LANGUAGE plpgsql;
        """);
      await File.WriteAllTextAsync(Path.Combine(dir.FullName, "002_b.sql"), """
        SELECT __SCHEMA__.drop_all_overloads('retired');
        CREATE OR REPLACE FUNCTION __SCHEMA__.calls_the_helper() RETURNS void AS $$
        BEGIN
          -- hashtextextended(event_id::text, 0) in a comment is not a copy
          PERFORM pg_advisory_xact_lock(hashtextextended('a lock key', 0));
          PERFORM __SCHEMA__._wh_fold_stream_digests(ARRAY[]::uuid[], ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::uuid[], ARRAY[]::uuid[], 1);
        END;
        $$ LANGUAGE plpgsql;
        """);

      var latest = _latestFunctionBodies(dir.FullName);

      await Assert.That(latest.Keys.Order(StringComparer.Ordinal).ToList())
        .IsEquivalentTo(["calls_the_helper", "copies_the_math"])
        .Because("a function retired by drop_all_overloads is no longer read");
      await Assert.That(DigestWrite().IsMatch(latest["copies_the_math"])).IsTrue();
      await Assert.That(DigestWrite().IsMatch(latest["calls_the_helper"]) || DigestHash().IsMatch(latest["calls_the_helper"]))
        .IsFalse().Because("a call to the helper, a lock key hash, and the math in a comment are not copies of the math");
    } finally {
      dir.Delete(recursive: true);
    }
  }

  /// <summary>Each function's latest body (comments removed), in migration order; functions retired later are left out.</summary>
  private static Dictionary<string, string> _latestFunctionBodies(string directory) {
    var latest = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var file in Directory.EnumerateFiles(directory, "*.sql").Order(StringComparer.Ordinal)) {
      var text = File.ReadAllText(file);
      var events = new List<(int At, string Name, string? Body)>();
      foreach (Match create in CreateFunction().Matches(text)) {
        events.Add((create.Index, create.Groups["name"].Value, _body(text, create.Index)));
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

  /// <summary>The dollar-quoted body after a CREATE FUNCTION header, with line comments removed.</summary>
  private static string _body(string text, int from) {
    var open = DollarQuote().Match(text, from);
    if (!open.Success) {
      return string.Empty;
    }
    var close = text.IndexOf(open.Value, open.Index + open.Length, StringComparison.Ordinal);
    var body = close < 0 ? text[(open.Index + open.Length)..] : text[(open.Index + open.Length)..close];
    return LineComment().Replace(body, string.Empty);
  }

  private static string _migrationsDirectory() =>
    Path.Combine(_repositoryRoot(), "src", "Whizbang.Data.Postgres", "Migrations");

  /// <summary>The repository root, found by walking up to the directory holding <c>src</c> and <c>tests</c>.</summary>
  private static string _repositoryRoot() {
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null
        && !(Directory.Exists(Path.Combine(dir.FullName, "src"))
          && Directory.Exists(Path.Combine(dir.FullName, "tests")))) {
      dir = dir.Parent;
    }
    return dir?.FullName
      ?? throw new InvalidOperationException("could not find the repository root, so the guard cannot scan what it guards");
  }
}
