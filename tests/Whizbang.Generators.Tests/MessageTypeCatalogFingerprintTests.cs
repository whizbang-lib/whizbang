// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests that MessageTypeCatalogGenerator stamps deterministic per-type content hashes (settings + schema)
/// onto each catalog entry (fingerprint F-3). Determinism is load-bearing — an identical definition must
/// hash identically across builds — so these lock stability, change-sensitivity, and the sourced/ephemeral
/// settings distinction.
/// </summary>
/// <tests>Whizbang.Generators/MessageTypeCatalogGenerator.cs</tests>
public class MessageTypeCatalogFingerprintTests {
  private static async Task<string> _generateAsync(string source) {
    var result = GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(source);
    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(errors).IsEmpty();
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTypeCatalog.g.cs");
    await Assert.That(code).IsNotNull();
    return code!;
  }

  // Pulls the hex value of SettingsHash/SchemaHash off the generated entry line for a given type.
  private static string _extract(string code, string typeName, string field) {
    var line = code.Split('\n').FirstOrDefault(l => l.Contains($"typeof(global::{typeName})")) ?? "";
    var m = Regex.Match(line, field + " = \"([0-9a-f]*)\"");
    return m.Success ? m.Groups[1].Value : "";
  }

  [Test]
  public async Task Fingerprint_StampsBothHashesOnEveryEntryAsync() {
    var code = await _generateAsync("""
      using Whizbang.Core;
      namespace MyApp;
      public record OrderPlaced(int OrderId, string Sku) : IEvent;
""");
    await Assert.That(_extract(code, "MyApp.OrderPlaced", "SettingsHash")).IsNotEmpty().Because("Every entry carries a settings hash.");
    await Assert.That(_extract(code, "MyApp.OrderPlaced", "SchemaHash")).IsNotEmpty().Because("Every entry carries a schema hash.");
  }

  [Test]
  public async Task SchemaHash_IsDeterministic_SameSourceSameHashAsync() {
    const string src = """
      using Whizbang.Core;
      namespace MyApp;
      public record OrderPlaced(int OrderId, string Sku) : IEvent;
""";
    var a = _extract(await _generateAsync(src), "MyApp.OrderPlaced", "SchemaHash");
    var b = _extract(await _generateAsync(src), "MyApp.OrderPlaced", "SchemaHash");
    await Assert.That(a).IsEqualTo(b).Because("An identical definition must hash identically across runs.");
  }

  [Test]
  public async Task SchemaHash_ChangesWhenAPropertyIsAddedAsync() {
    var oneProp = _extract(await _generateAsync("""
      using Whizbang.Core;
      namespace MyApp;
      public record OrderPlaced(int OrderId) : IEvent;
"""), "MyApp.OrderPlaced", "SchemaHash");
    var twoProps = _extract(await _generateAsync("""
      using Whizbang.Core;
      namespace MyApp;
      public record OrderPlaced(int OrderId, string Sku) : IEvent;
"""), "MyApp.OrderPlaced", "SchemaHash");
    await Assert.That(twoProps).IsNotEqualTo(oneProp).Because("Adding a property changes the payload schema.");
  }

  [Test]
  public async Task SchemaHash_ChangesWhenAPropertyTypeChangesAsync() {
    var asInt = _extract(await _generateAsync("""
      using Whizbang.Core;
      namespace MyApp;
      public record OrderPlaced(int OrderId) : IEvent;
"""), "MyApp.OrderPlaced", "SchemaHash");
    var asString = _extract(await _generateAsync("""
      using Whizbang.Core;
      namespace MyApp;
      public record OrderPlaced(string OrderId) : IEvent;
"""), "MyApp.OrderPlaced", "SchemaHash");
    await Assert.That(asString).IsNotEqualTo(asInt).Because("Retyping a property changes the payload schema.");
  }

  [Test]
  public async Task SettingsHash_DiffersEphemeralVsSourced_ButSameForTwoSourcedAsync() {
    var code = await _generateAsync("""
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      namespace MyApp;
      public record OrderPlaced(int OrderId) : IEvent;
      public record InvoiceRaised(int InvoiceId) : IEvent;
      [Ephemeral(Destruction = Destruction.WhenConsumed, Storage = TransientStorage.TtlRow)]
      public record UserIsTyping(int UserId) : IEvent;
""");
    var sourcedA = _extract(code, "MyApp.OrderPlaced", "SettingsHash");
    var sourcedB = _extract(code, "MyApp.InvoiceRaised", "SettingsHash");
    var ephemeral = _extract(code, "MyApp.UserIsTyping", "SettingsHash");

    await Assert.That(sourcedB).IsEqualTo(sourcedA).Because("Two Sourced types share the same settings hash ('sourced').");
    await Assert.That(ephemeral).IsNotEqualTo(sourcedA).Because("An ephemeral type's settings hash differs from a Sourced one.");
  }
  /// <summary>
  /// #2110: declaring a type [Superseding] is a settings change, so the type-definition registry records a new definition
  /// and the superseding-history reconcile can tell that the type's earlier events were stored under other settings. A type
  /// that is not superseding keeps exactly the hash it had, so no other type drifts on upgrade.
  /// </summary>
  [Test]
  public async Task SettingsHash_ChangesWhenATypeBecomesSuperseding_AndASourcedTypeKeepsItsHashAsync() {
    var code = await _generateAsync("""
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      namespace MyApp;
      public record OrderPlaced(int OrderId) : IEvent;
      [Superseding]
      public record ProfileReplaced(int UserId) : IEvent;
""");
    var sourced = _extract(code, "MyApp.OrderPlaced", "SettingsHash");
    var superseding = _extract(code, "MyApp.ProfileReplaced", "SettingsHash");

    await Assert.That(superseding).IsNotEqualTo(sourced).Because("becoming superseding is a settings change the registry must see");
    await Assert.That(sourced).IsEqualTo(_sha256Hex("sourced"))
      .Because("a type that is not superseding hashes exactly as before, so upgrading registers no new definition for it");
    await Assert.That(superseding).IsEqualTo(_sha256Hex("sourced;superseding"));
  }

  private static string _sha256Hex(string canonical) =>
    System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
}
