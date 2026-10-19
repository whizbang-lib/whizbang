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
/// A generic message type never makes the generators emit code that does not compile (#1481). The open type has no
/// closed form to name, so the generators skip it, and WHIZ015 says so at its declaration. A closed form a receptor
/// handles is still routed for local dispatch.
/// </summary>
/// <tests>src/Whizbang.Generators/MessageTypeCatalogGenerator.cs</tests>
/// <tests>src/Whizbang.Generators/ReceptorDiscoveryGenerator.cs</tests>
[Category("SourceGenerators")]
public class GenericMessageTypeGenerationTests {
  // The implicit usings an SDK project gets, which the generated dispatcher relies on.
  private const string IMPLICIT_USINGS = """
    global using System;
    global using System.Collections.Generic;
    global using System.Linq;
    global using System.Threading;
    global using System.Threading.Tasks;

    """;

  private const string GENERIC_MESSAGE = IMPLICIT_USINGS + """
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Whizbang.Core;

    namespace Shop;

    public interface IAnswer : IMessage { Guid Id { get; } }
    public record GenericAnswer<T>(Guid Id, T Value) : IAnswer;
    public record GenericEvent<T>(T Value) : IEvent;
    public record GenericCommand<T>(T Value) : ICommand;

    public record OrderPlaced(Guid Id) : IEvent;
    public class OrderPlacedReceptor : IReceptor<OrderPlaced> {
      public ValueTask HandleAsync(OrderPlaced message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
    """;

  private const string CLOSED_GENERIC_RECEPTOR = GENERIC_MESSAGE + """

    public class IntAnswerReceptor : IReceptor<GenericCommand<int>, GenericAnswer<int>> {
      public ValueTask<GenericAnswer<int>> HandleAsync(GenericCommand<int> message, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new GenericAnswer<int>(Guid.Empty, message.Value));
    }
    """;

  /// <summary>Every generator in the package, run together, so a generator's output that names a sibling's compiles.</summary>
  private static IIncrementalGenerator[] _allGenerators() =>
    [.. typeof(MessageTypeCatalogGenerator).Assembly.GetTypes()
      .Where(type => !type.IsAbstract && type.GetCustomAttribute<GeneratorAttribute>() is not null
        && typeof(IIncrementalGenerator).IsAssignableFrom(type))
      .Select(type => (IIncrementalGenerator)Activator.CreateInstance(type)!)];

  private const string NO_GENERICS = IMPLICIT_USINGS + """
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Whizbang.Core;

    namespace Shop;

    public record OrderPlaced(Guid Id) : IEvent;
    public class OrderPlacedReceptor : IReceptor<OrderPlaced> {
      public ValueTask HandleAsync(OrderPlaced message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
    """;

  /// <summary>The same project without the generic types compiles, so an error below comes from the generic types.</summary>
  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the generators by reflection over the generator assembly.")]
  public async Task WithoutGenericTypes_GeneratedCodeCompilesAsync() {
    var errors = GeneratorTestHelper.GetGeneratedCompilationErrors(_allGenerators(), NO_GENERICS);

    await Assert.That(_byFile(errors)).IsEmpty();
  }

  /// <summary>Open generic message types are skipped, and the generated code compiles.</summary>
  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the generators by reflection over the generator assembly.")]
  public async Task OpenGenericMessageTypes_GeneratedCodeCompilesAsync() {
    var errors = GeneratorTestHelper.GetGeneratedCompilationErrors(_allGenerators(), GENERIC_MESSAGE);

    await Assert.That(_byFile(errors)).IsEmpty();
  }

  /// <summary>A receptor of a closed generic message is discovered, and the generated code compiles.</summary>
  [Test]
  [RequiresAssemblyFiles]
  [RequiresUnreferencedCode("Finds the generators by reflection over the generator assembly.")]
  public async Task ClosedGenericMessageReceptor_GeneratedCodeCompilesAsync() {
    var errors = GeneratorTestHelper.GetGeneratedCompilationErrors(_allGenerators(), CLOSED_GENERIC_RECEPTOR);

    await Assert.That(_byFile(errors)).IsEmpty();
  }

  // One line per generated file with errors, naming its first error, so a failure lists every generator at fault.
  private static List<string> _byFile(IEnumerable<Diagnostic> errors) =>
    [.. errors.GroupBy(static e => Path.GetFileName(e.Location.SourceTree?.FilePath ?? "(no file)"))
      .Select(static g => $"{g.Key}: {g.Count()} error(s), first {g.First().Id} {g.First().GetMessage(System.Globalization.CultureInfo.InvariantCulture)}")
      .Order(StringComparer.Ordinal)];

  /// <summary>The closed form a receptor handles is still routed: the dispatcher names it.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task ClosedGenericMessageReceptor_IsRoutedByTheDispatcherAsync() {
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(CLOSED_GENERIC_RECEPTOR);
    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");

    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(dispatcher).Contains("global::Shop.GenericCommand<int>");
    await Assert.That(dispatcher).DoesNotContain("GenericCommand<T>");
  }
}
