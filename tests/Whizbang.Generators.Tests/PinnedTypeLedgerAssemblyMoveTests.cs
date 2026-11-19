// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// #2264: the pinned-type ledger records which assembly each entry's type is compiled in, so a type that moves to another
/// assembly (its ledger entry moving with it) has the names it was stored under recorded with the assembly it was stored
/// in. The event store's <c>event_type</c> carries the assembly (<c>Ns.Type, Assembly</c>), so a bare former name, which
/// means "this assembly", would name history that was never written. A rename within one assembly keeps its former names
/// bare, as before.
/// </summary>
/// <docs>fundamentals/identity/pinned-type-ledger#a-type-that-moves-to-another-assembly</docs>
/// <code-under-test>src/Whizbang.Generators/Ledger/PinnedTypeLedger.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/PinnedTypeLedgerGenerator.cs</code-under-test>
[Category("SourceGenerators")]
[Category("RenamePlatform")]
public class PinnedTypeLedgerAssemblyMoveTests {
  private const string LEDGER_PATH = "/repo/src/TestAssembly/.whizbang/pinned-type-ledger.json";
  private const string PINNED_ID = "11111111-1111-1111-1111-111111111111";

  // The test compilation is named TestAssembly; this is the assembly the type was compiled in before it moved.
  private const string FORMER_ASSEMBLY = "Former.Contracts";

  private const string ORDER_PLACED = """
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      namespace TestApp;
      [PinnedId("11111111-1111-1111-1111-111111111111")]
      public record OrderPlacedEvent : IEvent;
      """;

  [Test]
  [RequiresAssemblyFiles]
  public async Task ANewEntry_RecordsTheAssemblyItIsCompiledInAsync() {
    var entry = _onlyEntry(GeneratorTestHelper.RunGenerator<PinnedTypeLedgerGenerator>(ORDER_PLACED));

    await Assert.That(entry.GetProperty("assembly").GetString()).IsEqualTo("TestAssembly");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AnEntryWrittenBeforeAssembliesWereRecorded_IsTakenToBeInThisAssembly_AndKeepsItsFormerNamesBareAsync() {
    var entry = _onlyEntry(GeneratorTestHelper.RunGenerator<PinnedTypeLedgerGenerator>(
      ORDER_PLACED, [(LEDGER_PATH, _ledger("TestApp.OrderPlacedEvent", assembly: null, "TestApp.OrderCreatedEvent"))]));

    await Assert.That(entry.GetProperty("assembly").GetString()).IsEqualTo("TestAssembly");
    await Assert.That(_formerNames(entry)).IsEquivalentTo(["TestApp.OrderCreatedEvent"])
      .Because("an entry with no recorded assembly keeps today's meaning: its former names lived in this assembly");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ARenameWithinTheAssembly_KeepsItsFormerNamesBareAsync() {
    var entry = _onlyEntry(GeneratorTestHelper.RunGenerator<PinnedTypeLedgerGenerator>(
      ORDER_PLACED, [(LEDGER_PATH, _ledger("TestApp.OrderPlacedEvent", "TestAssembly", "TestApp.OrderCreatedEvent"))]));

    await Assert.That(entry.GetProperty("assembly").GetString()).IsEqualTo("TestAssembly");
    await Assert.That(_formerNames(entry)).IsEquivalentTo(["TestApp.OrderCreatedEvent"]);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AnEntryThatMovedHere_RecordsEveryNameItWasStoredUnder_WithTheAssemblyItWasStoredInAsync() {
    var entry = _onlyEntry(GeneratorTestHelper.RunGenerator<PinnedTypeLedgerGenerator>(
      ORDER_PLACED,
      [(LEDGER_PATH, _ledger("TestApp.OrderPlacedEvent", FORMER_ASSEMBLY, "TestApp.OrderCreatedEvent", "Older.OrderSubmitted, Oldest.Contracts"))]));

    await Assert.That(entry.GetProperty("assembly").GetString()).IsEqualTo("TestAssembly")
      .Because("the entry is now compiled here, so a later move starts from here");
    await Assert.That(entry.GetProperty("clrTypeName").GetString()).IsEqualTo("TestApp.OrderPlacedEvent");
    await Assert.That(_formerNames(entry)).IsEquivalentTo([
      "TestApp.OrderCreatedEvent, Former.Contracts",
      "Older.OrderSubmitted, Oldest.Contracts",
      "TestApp.OrderPlacedEvent, Former.Contracts",
    ]).Because("the type's events were stored under its old assembly, by its current name and by its former bare names; a "
             + "former name already recorded with an assembly keeps it");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AnEntryThatMovedHere_UnderANameItAlreadyRecordedWithTheOldAssembly_RecordsItOnceAsync() {
    var entry = _onlyEntry(GeneratorTestHelper.RunGenerator<PinnedTypeLedgerGenerator>(
      ORDER_PLACED, [(LEDGER_PATH, _ledger("TestApp.OrderPlacedEvent", FORMER_ASSEMBLY, "TestApp.OrderPlacedEvent, Former.Contracts"))]));

    await Assert.That(_formerNames(entry)).IsEquivalentTo(["TestApp.OrderPlacedEvent, Former.Contracts"]);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AnEntryThatMovedHere_AndWasRenamed_StillGovernsTheRenameAsync() {
    // The ledger still names the type as it was called in its old assembly; the merge never refreshes that name, so the
    // rename keeps tripping WHIZ120 until it is acknowledged, while the name it was stored under is already recorded.
    var entry = _onlyEntry(GeneratorTestHelper.RunGenerator<PinnedTypeLedgerGenerator>(
      ORDER_PLACED, [(LEDGER_PATH, _ledger("TestApp.OrderCreatedEvent", FORMER_ASSEMBLY))]));

    await Assert.That(entry.GetProperty("clrTypeName").GetString()).IsEqualTo("TestApp.OrderCreatedEvent");
    await Assert.That(_formerNames(entry)).IsEquivalentTo(["TestApp.OrderCreatedEvent, Former.Contracts"]);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ABareGenericFormerName_RecordsNoAssemblyAsync() {
    // A generic name carries commas inside its argument list; only a comma outside it introduces an assembly.
    const string generic = "TestApp.Wrapper`1[[TestApp.Inner, TestAssembly]]";
    var entry = _onlyEntry(GeneratorTestHelper.RunGenerator<PinnedTypeLedgerGenerator>(
      ORDER_PLACED, [(LEDGER_PATH, _ledger("TestApp.OrderPlacedEvent", FORMER_ASSEMBLY, generic))]));

    await Assert.That(_formerNames(entry)).Contains($"{generic}, Former.Contracts");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AnUnnamedAssembly_RecordsNoAssembly_AndLeavesAnExistingEntryAsItWasAsync() {
    var result = GeneratorTestHelper.RunGeneratorOnUnnamedAssembly(
      new PinnedTypeLedgerGenerator(), ORDER_PLACED, [(LEDGER_PATH, _ledger("TestApp.OrderPlacedEvent", FORMER_ASSEMBLY, "TestApp.OrderCreatedEvent"))]);
    var entry = _onlyEntry(result);

    await Assert.That(entry.GetProperty("assembly").GetString()).IsEqualTo(FORMER_ASSEMBLY);
    await Assert.That(_formerNames(entry)).IsEquivalentTo(["TestApp.OrderCreatedEvent"])
      .Because("with no assembly name to compare there is no move to record");

    var fresh = _onlyEntry(GeneratorTestHelper.RunGeneratorOnUnnamedAssembly(new PinnedTypeLedgerGenerator(), ORDER_PLACED));
    await Assert.That(fresh.TryGetProperty("assembly", out _)).IsFalse().Because("an unknown assembly is not written");
  }

  // ===== Helpers =====

  private static string _ledger(string clrTypeName, string? assembly, params string[] formerNames) {
    var assemblyField = assembly is null ? "" : $"\"assembly\": \"{assembly}\",";
    var former = string.Join(", ", formerNames.Select(n => $"\"{n}\""));
    return $$"""
      { "version": 1, "types": [
        { "pinnedId": "{{PINNED_ID}}", "clrTypeName": "{{clrTypeName}}", "kind": "event", {{assemblyField}}
          "formerNames": [{{former}}] }
      ] }
      """;
  }

  /// <summary>The ledger the generator emitted, read back from its C# constant, and its one entry.</summary>
  private static JsonElement _onlyEntry(Microsoft.CodeAnalysis.GeneratorDriverRunResult result) {
    var source = GeneratorTestHelper.GetGeneratedSource(result, "PinnedTypeLedger.g.cs")
      ?? throw new InvalidOperationException("the generator emitted no ledger");
    const string OPEN = "Json = @\"";
    var start = source.IndexOf(OPEN, StringComparison.Ordinal) + OPEN.Length;
    var end = source.LastIndexOf("\";", StringComparison.Ordinal);
    var json = source[start..end].Replace("\"\"", "\"", StringComparison.Ordinal);
    using var document = JsonDocument.Parse(json);
    return document.RootElement.GetProperty("types").EnumerateArray().Single().Clone();
  }

  private static string[] _formerNames(JsonElement entry) =>
    [.. entry.GetProperty("formerNames").EnumerateArray().Select(n => n.GetString()!)];
}
