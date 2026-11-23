// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.IO;
using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// The dispatcher creates a message id in one place (#2543): <c>_newMessageIdFor</c>, which derives it from the handling
/// a receptor body runs in and mints it outside one. A dispatch path that minted an id of its own would mint inside a
/// handling too, and a redelivered handling would then dispatch that message again under a new id. This reads the
/// dispatcher's source and fails on any other site that mints, so a new path cannot opt out by accident.
/// </summary>
/// <remarks>
/// The behavioral side is <c>DispatchSurfaceDerivedIdentityComponentTests</c>, one probe per dispatch method. Each scan
/// here is checked against a site it must find, so a scan that reads the wrong file fails instead of passing on nothing.
/// </remarks>
/// <docs>fundamentals/dispatcher/message-cascade#emission-identity</docs>
[Category("Dispatcher")]
[Category("Messaging")]
public partial class DispatcherMessageIdSourceGuardTests {
  private const string DISPATCHER = "src/Whizbang.Core/Dispatcher.cs";

  /// <summary>The members allowed to mint, and why each one may.</summary>
  private static readonly Dictionary<string, string> _mintingSites = new(StringComparer.Ordinal) {
    ["_newMessageIdFor"] = "the one place a dispatch creates a message id: minted only outside a handling",
    ["_mintEmissionId"] = "an event's id when the handling it derives from has no message id to derive from",
    ["_mintSentMessageId"] = "a command's id when the handling it derives from has no message id to derive from",
    ["PublishOnceAsync"] = "the claim's owner token is bookkeeping, not a message id",
  };

  [GeneratedRegex(@"\b(?:MessageId\.New|TrackedGuid\.New\w*|Guid\.NewGuid|Guid\.CreateVersion7|Uuid7Generator\.\w+\.NewGuid)\s*\(")]
  private static partial Regex _mint();

  [GeneratedRegex(@"^\s{2}(?:public|private|protected|internal)\b[^=;]*?\b(\w+)\s*(?:<[^>()]*>)?\s*\(")]
  private static partial Regex _memberDeclaration();

  [Test]
  public async Task EveryIdTheDispatcherMints_IsMintedByAnAllowedMemberAsync() {
    var lines = await File.ReadAllLinesAsync(Path.Combine(_findRepoRoot(), DISPATCHER));
    var sites = new List<(int Line, string Member)>();
    string? member = null;
    for (var i = 0; i < lines.Length; i++) {
      var line = lines[i];
      if (_memberDeclaration().Match(line) is { Success: true } declaration) {
        member = declaration.Groups[1].Value;
      }
      var trimmed = line.TrimStart();
      if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("///", StringComparison.Ordinal)) {
        continue;
      }
      if (_mint().IsMatch(line)) {
        sites.Add((i + 1, member ?? "(none)"));
      }
    }

    await Assert.That(sites.Select(static s => s.Member)).Contains("_newMessageIdFor")
      .Because("the scan must find the one helper, or it is not reading the dispatcher");
    var strays = sites.Where(s => !_mintingSites.ContainsKey(s.Member)).Select(static s => $"line {s.Line} in {s.Member}").ToList();
    await Assert.That(strays).IsEmpty()
      .Because("create a dispatched message's id with _newMessageIdFor, so it derives inside a handling (#2543)");
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
