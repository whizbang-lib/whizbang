// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// A <c>[TopicFilter(null)]</c> names no topic, so it contributes no filter: the command it
/// decorates is left out of the registry rather than being routed on an empty or "null" topic.
/// </summary>
/// <tests>src/Whizbang.Generators/TopicFilterGenerator.cs</tests>
[Category("SourceGenerators")]
public class TopicFilterGeneratorBranchTests {
  [Test]
  [RequiresAssemblyFiles]
  public async Task NullStringFilter_ContributesNoFilterAsync() {
    var result = GeneratorTestHelper.RunGenerator<TopicFilterGenerator>("""
      using Whizbang.Core;

      namespace TestNamespace;

      [TopicFilter(null!)]
      public record UnroutedCommand : ICommand;

      [TopicFilter("orders.create")]
      public record RoutedCommand : ICommand;
      """);

    var registry = GeneratorTestHelper.GetGeneratedSource(result, "TopicFilterRegistry.g.cs");

    await Assert.That(registry).IsNotNull();
    await Assert.That(registry).Contains("orders.create")
      .Because("the control: a named topic is registered");
    await Assert.That(registry).DoesNotContain("UnroutedCommand");
  }

  /// <summary>
  /// An argument that does not bind (a name nothing declares) carries neither a string nor an enum,
  /// so it names no topic either; source an IDE hands the generator mid-edit must not register one.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task FilterArgumentThatDoesNotBind_ContributesNoFilterAsync() {
    var result = GeneratorTestHelper.RunGenerator<TopicFilterGenerator>("""
      using Whizbang.Core;

      namespace TestNamespace;

      [TopicFilter(NothingDeclaresThis)]
      public record UnboundCommand : ICommand;

      [TopicFilter("orders.create")]
      public record RoutedCommand : ICommand;
      """);

    var registry = GeneratorTestHelper.GetGeneratedSource(result, "TopicFilterRegistry.g.cs");

    await Assert.That(registry).IsNotNull();
    await Assert.That(registry).Contains("orders.create")
      .Because("the control: a named topic is registered");
    await Assert.That(registry).DoesNotContain("UnboundCommand");
  }
}
