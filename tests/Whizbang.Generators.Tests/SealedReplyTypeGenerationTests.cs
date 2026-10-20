// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// A receptor whose reply is a sealed type that is not a message compiles. The generated outbox cascade switch cast
/// each reply type from <c>IMessage</c>, which C# refuses for a sealed type that does not implement it (CS0030), so a
/// sync or async receptor returning a sealed record broke the consumer's build. A non-sealed reply compiled, which is
/// why it went unnoticed.
/// </summary>
/// <tests>src/Whizbang.Generators/ReceptorDiscoveryGenerator.cs</tests>
[Category("SourceGenerators")]
public class SealedReplyTypeGenerationTests {
  private const string SEALED_REPLIES = """
    global using System;
    global using System.Collections.Generic;
    global using System.Linq;
    global using System.Threading;
    global using System.Threading.Tasks;

    using Whizbang.Core;

    namespace Shop;

    public record PlaceOrder(Guid Id) : ICommand;
    public record PriceOrder(Guid Id) : ICommand;
    public sealed record OrderPlacedReply(Guid Id);
    public sealed record OrderPrice(decimal Amount);

    public class PlaceOrderReceptor : IReceptor<PlaceOrder, OrderPlacedReply> {
      public ValueTask<OrderPlacedReply> HandleAsync(PlaceOrder message, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new OrderPlacedReply(message.Id));
    }

    public class PriceOrderReceptor : ISyncReceptor<PriceOrder, OrderPrice> {
      public OrderPrice Handle(PriceOrder message) => new(10m);
    }
    """;

  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the generators by reflection over the generator assembly.")]
  public async Task ReceptorsWithSealedNonMessageReplies_GeneratedCodeCompilesAsync() {
    var generators = typeof(MessageTypeCatalogGenerator).Assembly.GetTypes()
      .Where(type => !type.IsAbstract && type.GetCustomAttribute<GeneratorAttribute>() is not null
        && typeof(IIncrementalGenerator).IsAssignableFrom(type))
      .Select(type => (IIncrementalGenerator)Activator.CreateInstance(type)!)
      .ToArray();

    var errors = GeneratorTestHelper.GetGeneratedCompilationErrors(generators, SEALED_REPLIES);

    await Assert.That(errors.Select(e => $"{e.Id} {e.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}").ToList()).IsEmpty()
      .Because("a reply that is not a message never reaches the outbox cascade, so the generated switch must not cast to it");
  }
}
