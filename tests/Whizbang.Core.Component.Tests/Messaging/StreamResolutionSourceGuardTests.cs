// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.IO;
using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Source-level guard for the stream an event is stored on (#1798). Every decision goes through one helper
/// (<c>StreamIdentity</c>), and the cross-path parity table (<c>StreamResolutionParityTests</c>) covers every site that
/// builds an inbox row. This reads the source tree, so a new site cannot slip in unnoticed:
/// <list type="bullet">
/// <item><description>only <c>StreamIdentity</c> may read or write the hop key that names a message's stream;</description></item>
/// <item><description>every file that builds an inbox row is a row of the parity table and resolves the stream through the helper;</description></item>
/// <item><description>every file that builds an outbox row is listed here with how its stream is decided.</description></item>
/// </list>
/// Each scan is checked against a fact it must find (the helper's own file, the two known inbox builders), so a scan
/// that looks in the wrong place fails instead of passing on nothing.
/// </summary>
/// <docs>fundamentals/messaging/stream-identity</docs>
[Category("Core")]
[Category("Messaging")]
public partial class StreamResolutionSourceGuardTests {
  private static readonly string _repoRoot = _findRepoRoot();

  private const string HELPER = "src/Whizbang.Core/Messaging/StreamIdentity.cs";

  private const string PARITY_TABLE = "tests/Whizbang.Core.Tests/Messaging/StreamResolutionParityTests.cs";

  /// <summary>The inbox-row builders, each a row of the parity table.</summary>
  private static readonly string[] _inboxBuilders = [
    "src/Whizbang.Core/Workers/ReceivedInboxMessageBuilder.cs",
    "src/Whizbang.Core/Messaging/CompositeInboxFanout.cs",
  ];

  /// <summary>The outbox-row builders and how each decides the row's stream.</summary>
  private static readonly Dictionary<string, string> _outboxBuilders = new(StringComparer.Ordinal) {
    ["src/Whizbang.Core/Dispatcher.cs"] = "StreamIdentity.Of over the hop the dispatcher wrote, or the payload's [StreamId] the hop was written from",
    ["src/Whizbang.Core/SystemEvents/AuditOutboxMessageBuilder.cs"] = "the tenant's audit ledger lane; the hop names the record's own stream, which the receiver stores it on",
    ["src/Whizbang.Core/SystemEvents/AuditingEventStoreDecorator.cs"] = "the tenant's audit ledger lane; the hop names the record's own stream, which the receiver stores it on",
    ["src/Whizbang.Core/Workers/CoalesceShipWorker.cs"] = "the bundle's own stream; transport only, each child carries its own",
    ["src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs"] = "reads stored rows back; decides nothing",
  };

  [GeneratedRegex(@"""AggregateId""")]
  private static partial Regex _hopKeyLiteral();

  // A row built by name (qualified or not), by a target-typed new, or copied with a new stream.
  [GeneratedRegex(@"new\s+(?:[\w.]+\.)?InboxMessage\s*[{(]|InboxMessage\??\s+\w+\s*=\s*new\s*\(|with\s*\{[^}]*\bStreamId\s*=")]
  private static partial Regex _inboxRow();

  [GeneratedRegex(@"new\s+(?:[\w.]+\.)?OutboxMessage\s*[{(]|OutboxMessage\??\s+\w+\s*=\s*new\s*\(")]
  private static partial Regex _outboxRow();

  /// <summary>Only the helper reads or writes the hop key; a second reader is how three copies came to drift.</summary>
  [Test]
  public async Task HopStreamKey_IsReadAndWrittenOnlyByTheHelperAsync() {
    var sites = await _filesMatchingAsync(_hopKeyLiteral(), skipComments: true);

    await Assert.That(sites).Contains(HELPER).Because("the scan must find the helper itself, or it is not reading the source tree");
    await Assert.That(sites).IsEquivalentTo([HELPER])
      .Because("read a message's stream with StreamIdentity.FromHops/Of and write it with StreamIdentity.HopMetadata");
  }

  /// <summary>Every inbox-row builder is a parity row and takes the stream from the helper.</summary>
  [Test]
  public async Task InboxRowBuilders_AreTheParityTablesAndUseTheHelperAsync() {
    var sites = await _filesMatchingAsync(_inboxRow(), skipComments: true);

    await Assert.That(sites).IsEquivalentTo(_inboxBuilders)
      .Because("a new place that stores a received event needs a row in StreamResolutionParityTests and an entry here");
    var parityTable = await File.ReadAllTextAsync(Path.Combine(_repoRoot, PARITY_TABLE));
    foreach (var site in _inboxBuilders) {
      await Assert.That(parityTable).Contains($"\"{site}\"").Because($"the parity table names {site} among the inbox builders it covers");
    }
    foreach (var site in sites) {
      var text = await File.ReadAllTextAsync(Path.Combine(_repoRoot, site));
      await Assert.That(text).Contains("StreamIdentity.").Because($"{site} decides the stream through the shared helper");
    }
  }

  /// <summary>Every outbox-row builder is listed with how it decides the stream.</summary>
  [Test]
  public async Task OutboxRowBuilders_AreEachAccountedForAsync() {
    var sites = await _filesMatchingAsync(_outboxRow(), skipComments: true);

    await Assert.That(sites).Contains("src/Whizbang.Core/Dispatcher.cs").Because("the scan must find the dispatcher's outbox rows");
    await Assert.That(sites).IsEquivalentTo(_outboxBuilders.Keys)
      .Because("a new place that writes an outbox row decides a stream a receiver will store on; add it here, and a row "
             + "to StreamResolutionParityTests when the receiver stores it");
  }

  private static async Task<List<string>> _filesMatchingAsync(Regex pattern, bool skipComments) {
    var hits = new List<string>();
    foreach (var path in Directory.EnumerateFiles(Path.Combine(_repoRoot, "src"), "*.cs", SearchOption.AllDirectories)) {
      var relative = Path.GetRelativePath(_repoRoot, path).Replace('\\', '/');
      if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)
          || relative.Contains("/.whizbang/", StringComparison.Ordinal) || relative.Contains("/Templates/", StringComparison.Ordinal)) {
        continue;
      }
      var lines = await File.ReadAllLinesAsync(path);
      if (lines.Any(l => (!skipComments || !l.TrimStart().StartsWith("//", StringComparison.Ordinal)) && pattern.IsMatch(l))) {
        hits.Add(relative);
      }
    }
    hits.Sort(StringComparer.Ordinal);
    return hits;
  }

  private static string _findRepoRoot() {
    var dir = AppContext.BaseDirectory;
    while (dir is not null) {
      if (Directory.Exists(Path.Combine(dir, "src", "Whizbang.Core"))) {
        return dir;
      }
      dir = Path.GetDirectoryName(dir);
    }
    throw new InvalidOperationException("Could not locate whizbang repo root from AppContext.BaseDirectory");
  }
}
