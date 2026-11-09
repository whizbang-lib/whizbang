// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Issue #2072: a <c>[WhizbangId]</c> strong id used as a message's stream id. A generic base message whose
/// <c>[StreamId]</c> is a type parameter, and the concrete messages that close it with a strong id, compile in the
/// stream-id and JSON-context generators.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/StreamIdGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/MessageJsonContextGenerator.cs</code-under-test>
[Category("SourceGenerators")]
public class StrongIdStreamIdGeneratorTests {
  private const string EXTRACTORS = "StreamIdExtractors.g.cs";

  /// <summary>
  /// The stream-id generator, the JSON-context generator and the strong-id generator that emits the id types both
  /// of them reference.
  /// </summary>
  [RequiresAssemblyFiles]
  private static ImmutableArray<Diagnostic> _compileErrors(string source) =>
    GeneratorTestHelper.GetGeneratedCompilationErrors(
      [new StreamIdGenerator(), new MessageJsonContextGenerator(), new WhizbangIdGenerator()], source);

  private const string GENERIC_BASE = """
    using System;
    using Whizbang.Core;

    namespace TestApp.Contracts;

    [WhizbangId]
    public readonly partial struct ThingId;

    public abstract class BaseCommand<TStreamId> : ICommand where TStreamId : struct {
      [StreamId]
      public TStreamId StreamId { get; set; }
    }

    public abstract class BaseEvent<TStreamId> : IEvent where TStreamId : struct {
      [StreamId]
      public TStreamId StreamId { get; set; }
    }

    public class CreateThing : BaseCommand<ThingId> {
      public string Name { get; set; } = "";
    }

    public class ThingCreated : BaseEvent<ThingId> {
      public string Name { get; set; } = "";
    }
    """;

  [Test]
  [RequiresAssemblyFiles]
  public async Task GenericBaseWithAStrongIdStreamId_TheGeneratedCodeCompilesAsync() {
    var errors = _compileErrors(GENERIC_BASE);

    await Assert.That(errors).IsEmpty()
      .Because("an open generic base names a type parameter no generated code can refer to");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task GenericBaseWithAStrongIdStreamId_TheClosedMessagesRouteByTheirStrongIdAsync() {
    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(GENERIC_BASE);
    var code = GeneratorTestHelper.GetRequiredGeneratedSource(result, EXTRACTORS);

    await Assert.That(code).Contains("command is global::TestApp.Contracts.CreateThing");
    await Assert.That(code).Contains("@event is global::TestApp.Contracts.ThingCreated");
    await Assert.That(code).DoesNotContain("TStreamId");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AConcreteGenericBase_TheJsonContextCompilesAsync() {
    // A base that is not abstract stays a polymorphic base over its messages; its factory's name is built from the
    // constructed type's name, which holds characters an identifier cannot.
    var errors = GeneratorTestHelper.GetGeneratedCompilationErrors(
      [new MessageJsonContextGenerator(), new WhizbangIdGenerator()], """
      using Whizbang.Core;

      namespace TestApp;

      public class EventBase<T> : IEvent {
        public T? Value { get; init; }
      }

      public class CountedEvent : EventBase<int> {
        public string Label { get; init; } = "";
      }
      """);

    await Assert.That(errors).IsEmpty()
      .Because("the polymorphic factory for EventBase<int> must be named with identifier characters only");
  }
}
