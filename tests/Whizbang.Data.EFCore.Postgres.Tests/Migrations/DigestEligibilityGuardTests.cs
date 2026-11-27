// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// #2352: which events a digest counts is one rule, written once. Every SQL site writes the
/// <c>__DIGEST_ELIGIBLE__(alias)</c> migration constant (and every checkpoint count <c>__INTEGRITY_COUNTED__(alias)</c>),
/// and C# SQL reads the same constants through <see cref="MigrationConstants"/>. A hand-written copy of the rule (an
/// ephemeral or at-most-once flag test, or the exempt-type list) in a function that folds or compares digests, or in
/// C# SQL, is how five copies came to disagree, so a copy fails this guard by shape.
/// </summary>
/// <remarks>
/// A flag test that is not the counting rule (selecting the rows to flag, for example) is allowed when the line above
/// it says why, with a comment containing <c>not the digest rule:</c>. The scan reads each function's LATEST definition
/// across the migrations, as <c>StreamDigestFoldGuardTests</c> does.
/// </remarks>
/// <docs>resilience/stream-integrity#one-eligibility-rule</docs>
[Category("Shard3")]
public partial class DigestEligibilityGuardTests {
  private const string MARKER = "not the digest rule:";

  [GeneratedRegex(@"CREATE\s+OR\s+REPLACE\s+FUNCTION\s+__SCHEMA__\.(?<name>[a-z_0-9]+)\s*\(", RegexOptions.IgnoreCase)]
  private static partial Regex CreateFunction();

  [GeneratedRegex(@"drop_all_overloads\s*\(\s*'(?<name>[a-z_0-9]+)'\s*\)", RegexOptions.IgnoreCase)]
  private static partial Regex DropAllOverloads();

  [GeneratedRegex(@"\$(?<tag>[a-z_]*)\$", RegexOptions.IgnoreCase)]
  private static partial Regex DollarQuote();

  // A function that folds, compares or stores digests.
  [GeneratedRegex(@"_wh_digest_lane|_wh_fold_stream_digests|wh_stream_digests|wh_unsequenced_digest", RegexOptions.IgnoreCase)]
  private static partial Regex DigestContext();

  // A piece of the rule written by hand: the ephemeral (8) or at-most-once (32) flag test, or the exempt-type list.
  // #2588: and the integrity-excluded flag (256).
  [GeneratedRegex(@"&\s*\(?\s*(8|32|256|c_flag_ephemeral|c_flag_at_most_once|c_flag_integrity_excluded|v_flag_ephemeral|v_flag_at_most_once)\b|__DIGEST_EXEMPT_TYPES__", RegexOptions.IgnoreCase)]
  private static partial Regex SqlRuleCopy();

  // The same in C# SQL: the flag tests, or the exempt list bound as a parameter.
  [GeneratedRegex(@"flags,\s*0\)\s*&\s*((8|32|256)\b|\{AT_MOST_ONCE_FLAG\})|p_digest_exempt|DigestExemptTypes\.TypeNames")]
  private static partial Regex CSharpRuleCopy();

  [Test]
  public async Task NoDigestFunction_WritesTheEligibilityRuleByHandAsync() {
    var latest = _latestFunctionBodies(_migrationsDirectory());
    await Assert.That(latest.Keys.Count).IsGreaterThan(100)
      .Because("precondition: the scan found the migrations' functions, or it would pass by reading nothing");
    await Assert.That(latest.Count(f => DigestContext().IsMatch(f.Value))).IsGreaterThan(15)
      .Because("precondition: the digest functions are among those read");

    var offenders = _offenders(latest);

    await Assert.That(offenders).IsEmpty()
      .Because("these digest functions test a flag or the exempt list themselves instead of writing "
        + "__DIGEST_ELIGIBLE__(alias), so a change to what counts made in one place misses them: " + string.Join(", ", offenders));
  }

  [Test]
  public async Task NoCSharpSql_WritesTheEligibilityRuleByHandAsync() {
    var root = _repositoryRoot();
    var offenders = new SortedSet<string>(StringComparer.Ordinal);
    foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)) {
      if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
          || file.EndsWith($"{Path.DirectorySeparatorChar}DigestExemptTypes.cs", StringComparison.Ordinal)
          // The rule's one source (#2343): the migrations' constants are held to it by EligibilitySqlVersionTests.
          || file.EndsWith($"{Path.DirectorySeparatorChar}Whizbang.Rules{Path.DirectorySeparatorChar}EligibilitySql.cs", StringComparison.Ordinal)) {
        continue;
      }
      var lines = await File.ReadAllLinesAsync(file);
      for (var i = 0; i < lines.Length; i++) {
        var trimmed = lines[i].TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*') || !CSharpRuleCopy().IsMatch(lines[i])
            || (i > 0 && lines[i - 1].Contains(MARKER, StringComparison.Ordinal))) {
          continue;
        }
        offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}");
      }
    }

    await Assert.That(offenders).IsEmpty()
      .Because("C# SQL reads the rule from MigrationConstants (__DIGEST_ELIGIBLE__ / __INTEGRITY_COUNTED__), never its own "
        + "copy: " + string.Join(", ", offenders));
  }

  [Test]
  public async Task TheConstants_ExpandToTheRule_UnderTheAliasGivenAsync() {
    var eligible = MigrationConstants.Apply("__DIGEST_ELIGIBLE_V2__(es)");
    var counted = MigrationConstants.Apply("__INTEGRITY_COUNTED_V2__(NEW)");

    // #2588: version 2 leaves out an operator's exclusion (EventFlags.IntegrityExcluded, 256) on both.
    await Assert.That(eligible).IsEqualTo(
      "(COALESCE(es.flags, 0) & 8 = 0 AND COALESCE(es.flags, 0) & 32 = 0 AND COALESCE(es.flags, 0) & 256 = 0 AND "
      + "split_part(es.event_type, ',', 1) <> ALL(" + MigrationConstants.Tokens["__DIGEST_EXEMPT_TYPES__"] + "))");
    await Assert.That(counted).IsEqualTo(
      "(COALESCE(NEW.flags, 0) & 32 = 0 AND COALESCE(NEW.flags, 0) & 256 = 0 AND split_part(NEW.event_type, ',', 1) <> ALL("
      + MigrationConstants.Tokens["__DIGEST_EXEMPT_TYPES__"] + "))");
    await Assert.That(MigrationConstants.UnknownTokens("WHERE __DIGEST_ELIGIBLE_V2__(es) AND __INTEGRITY_COUNTED_V2__(es)")).IsEmpty();
  }

  /// <summary>
  /// #2588: the rule's version 1 tokens stay defined only because shipped migrations write them; the latest definition of
  /// every function reads the current version, so no fold counts by a rule the fingerprint no longer names.
  /// </summary>
  [Test]
  public async Task NoFunctionsLatestDefinition_ReadsARetiredRuleVersionAsync() {
    var latest = _latestFunctionBodies(_migrationsDirectory());
    await Assert.That(latest.Count(f => f.Value.Contains("__DIGEST_ELIGIBLE_V2__(", StringComparison.Ordinal))).IsGreaterThan(25)
      .Because("precondition: the readers of the rule are among the functions read");

    List<string> offenders = [.. latest
      .Where(f => f.Value.Contains("__DIGEST_ELIGIBLE__(", StringComparison.Ordinal)
        || f.Value.Contains("__INTEGRITY_COUNTED__(", StringComparison.Ordinal))
      .Select(f => f.Key)
      .Order(StringComparer.Ordinal)];

    await Assert.That(offenders).IsEmpty()
      .Because("these functions still count by version 1 of the eligibility rule, which leaves an excluded event in: "
        + string.Join(", ", offenders));
  }

  [Test]
  public async Task TheGuard_FlagsAHandWrittenRule_AndPassesTheConstantOrAMarkedFlagTestAsync() {
    var dir = Directory.CreateTempSubdirectory("eligibility-guard-");
    try {
      await File.WriteAllTextAsync(Path.Combine(dir.FullName, "001_a.sql"), """
        CREATE OR REPLACE FUNCTION __SCHEMA__.copies_the_rule() RETURNS void AS $$
        BEGIN
          PERFORM __SCHEMA__._wh_fold_stream_digests(NULL, NULL, NULL, NULL, NULL, 'fold', NOW())
          FROM __SCHEMA__.wh_event_store es WHERE COALESCE(es.flags, 0) & 8 = 0;
        END;
        $$ LANGUAGE plpgsql;
        CREATE OR REPLACE FUNCTION __SCHEMA__.not_a_digest_function() RETURNS void AS $$
        BEGIN
          UPDATE __SCHEMA__.wh_event_store SET flags = flags | 32 WHERE flags & 32 = 0;
        END;
        $$ LANGUAGE plpgsql;
        """);
      await File.WriteAllTextAsync(Path.Combine(dir.FullName, "002_b.sql"), """
        CREATE OR REPLACE FUNCTION __SCHEMA__.uses_the_constant() RETURNS void AS $$
        BEGIN
          -- not the digest rule: selects the rows still to flag
          UPDATE __SCHEMA__.wh_event_store es SET flags = flags | 32 WHERE es.flags & 32 = 0;
          -- COALESCE(es.flags, 0) & 8 = 0 in a comment is not a copy
          PERFORM __SCHEMA__._wh_fold_stream_digests(NULL, NULL, NULL, NULL, NULL, 'fold', NOW())
          FROM __SCHEMA__.wh_event_store es WHERE __DIGEST_ELIGIBLE__(es);
        END;
        $$ LANGUAGE plpgsql;
        """);

      var offenders = _offenders(_latestFunctionBodies(dir.FullName));

      await Assert.That(offenders).IsEquivalentTo(["copies_the_rule"])
        .Because("a flag test outside a digest function, the constant, a marked flag test and a comment are not copies");
    } finally {
      dir.Delete(recursive: true);
    }
  }

  private static List<string> _offenders(Dictionary<string, string> latest) =>
    [.. latest
      .Where(f => DigestContext().IsMatch(_withoutComments(f.Value)) && _copiesTheRule(f.Value))
      .Select(f => f.Key)
      .Order(StringComparer.Ordinal)];

  /// <summary>A line (not a comment) testing a flag or the exempt list, without a marker on the line above it.</summary>
  private static bool _copiesTheRule(string body) {
    var lines = body.Split('\n');
    for (var i = 0; i < lines.Length; i++) {
      var code = _withoutComments(lines[i]);
      if (SqlRuleCopy().IsMatch(code) && !(i > 0 && lines[i - 1].Contains(MARKER, StringComparison.Ordinal))) {
        return true;
      }
    }
    return false;
  }

  private static string _withoutComments(string sql) => LineComment().Replace(sql, string.Empty);

  [GeneratedRegex(@"--[^\n]*")]
  private static partial Regex LineComment();

  /// <summary>Each function's latest body (comments kept), in migration order; functions retired later are left out.</summary>
  private static Dictionary<string, string> _latestFunctionBodies(string directory) {
    var latest = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var file in Directory.EnumerateFiles(directory, "*.sql").Where(f => !f.EndsWith(".down.sql", StringComparison.Ordinal))
               .Order(StringComparer.Ordinal)) {
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

  private static string _body(string text, int from) {
    var open = DollarQuote().Match(text, from);
    if (!open.Success) {
      return string.Empty;
    }
    var close = text.IndexOf(open.Value, open.Index + open.Length, StringComparison.Ordinal);
    return close < 0 ? text[(open.Index + open.Length)..] : text[(open.Index + open.Length)..close];
  }

  private static string _migrationsDirectory() =>
    Path.Combine(_repositoryRoot(), "src", "Whizbang.Data.Postgres", "Migrations");

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
