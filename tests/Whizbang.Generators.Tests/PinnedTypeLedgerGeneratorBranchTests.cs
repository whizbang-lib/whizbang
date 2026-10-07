// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Only a concrete class or struct is a pinned type the ledger records: an abstract message and an
/// interface carrying <c>[PinnedId]</c> are never instantiated, so they are left out.
/// </summary>
/// <tests>src/Whizbang.Generators/PinnedTypeLedgerGenerator.cs</tests>
[Category("SourceGenerators")]
public class PinnedTypeLedgerGeneratorBranchTests {
  [Test]
  [RequiresAssemblyFiles]
  public async Task AbstractAndInterfaceTypes_AreNotRecordedAsync() {
    var result = GeneratorTestHelper.RunGenerator<PinnedTypeLedgerGenerator>("""
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      namespace TestApp;

      [PinnedId("11111111-1111-1111-1111-111111111111")]
      public record OrderPlacedEvent : IEvent;

      [PinnedId("44444444-4444-4444-4444-444444444444")]
      public abstract record OrderEventBase : IEvent;

      [PinnedId("55555555-5555-5555-5555-555555555555")]
      public interface IOrderEvent : IEvent { }
      """);
    var ledger = GeneratorTestHelper.GetGeneratedSource(result, "PinnedTypeLedger.g.cs");

    await Assert.That(ledger).IsNotNull();
    await Assert.That(ledger).Contains("11111111-1111-1111-1111-111111111111")
      .Because("the control: a concrete pinned event is recorded");
    await Assert.That(ledger).DoesNotContain("44444444-4444-4444-4444-444444444444");
    await Assert.That(ledger).DoesNotContain("55555555-5555-5555-5555-555555555555");
  }
}
