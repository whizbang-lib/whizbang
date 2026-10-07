// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Where a positional record's <c>[GenerateStreamId]</c> policy comes from (the parameter, else the
/// type, else nowhere), and a get-only stream id, which the framework cannot write a minted id into.
/// </summary>
/// <tests>src/Whizbang.Generators/StreamIdGenerator.cs</tests>
[Category("SourceGenerators")]
public class StreamIdGeneratorBranchTests {
  private static string _extractors(string declaration) {
    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>($$"""
      using System;
      using Whizbang.Core;

      namespace TestNamespace;

      {{declaration}}
      """);
    return GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs") ?? "";
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ParameterAttribute_SuppliesThePolicyAsync() {
    var source = _extractors("public record OrderCreated([StreamId][GenerateStreamId(OnlyIfEmpty = true)] Guid OrderId) : IEvent;");

    await Assert.That(source).Contains("OrderCreated");
    await Assert.That(source).Contains("(true, true)");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task TypeAttribute_SuppliesThePolicyWhenTheParameterHasNoneAsync() {
    var source = _extractors("[GenerateStreamId(OnlyIfEmpty = true)] public record OrderShipped([StreamId] Guid OrderId) : IEvent;");

    await Assert.That(source).Contains("OrderShipped");
    await Assert.That(source).Contains("(true, true)");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task NoAttribute_MeansNoGenerationAsync() {
    var source = _extractors("public record OrderClosed([StreamId] Guid OrderId) : IEvent;");

    await Assert.That(source).Contains("OrderClosed")
      .Because("the control: the stream id is still extracted");
    await Assert.That(source).DoesNotContain("(true, ");
  }

  /// <summary>
  /// A get-only stream id has no setter at all, so a minted id has nowhere to go: it is reported
  /// exactly as an init-only one is (WHIZ013).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task GetOnlyStreamIdWithGenerate_IsWhiz013Async() {
    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>("""
      using System;
      using Whizbang.Core;

      namespace TestNamespace;

      public class OrderOpened : IEvent {
        [StreamId] [GenerateStreamId]
        public Guid OrderId { get; } = Guid.Empty;
      }
      """);

    var whiz013 = result.Diagnostics.Where(d => d.Id == "WHIZ013").ToList();
    await Assert.That(whiz013).Count().IsEqualTo(1);
    await Assert.That(whiz013[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("'OrderId'");
  }
}
