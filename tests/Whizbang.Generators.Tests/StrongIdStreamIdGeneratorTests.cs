// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Issue #2072: a <c>[WhizbangId]</c> strong id used as a message's stream id. A generic base message whose
/// <c>[StreamId]</c> is a type parameter, and the concrete messages that close it with a strong id, compile in the
/// stream-id and JSON-context generators. The generated <c>SetStreamId</c> writes a strong-id stream id as a new id
/// wrapping the Guid, ahead of any inherited Guid stream id a base type declares; the probes compile the contracts
/// with the stream-id and strong-id generators, run them, and report which property each call wrote.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/StreamIdGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/MessageJsonContextGenerator.cs</code-under-test>
[Category("SourceGenerators")]
public class StrongIdStreamIdGeneratorTests {
  private const string EXTRACTORS = "StreamIdExtractors.g.cs";

  // A version 7 Guid, as the dispatcher mints.
  private static readonly Guid _mintedId = Guid.Parse("01890a5d-ac96-774b-bcce-b302099a8057");

  /// <summary>
  /// Compiles <paramref name="contracts"/> and <paramref name="probe"/> with the stream-id and strong-id generators,
  /// loads the result and returns what <c>App.Probe.Run()</c> returned.
  /// </summary>
  [RequiresAssemblyFiles]
  private static string _runProbe(string assemblyName, string contracts, string probe) {
    var source = $$"""
      using System;
      using Whizbang.Core;
      {{contracts}}
      namespace App {
        public static class Probe {
          public static string Run() {
            {{probe}}
          }
        }
      }
      """;
    var (_, output) = GeneratorTestHelper.RunGeneratorsWithFullReferences(
      [new StreamIdGenerator(), new WhizbangIdGenerator()], source, assemblyName);
    using var image = new MemoryStream();
    var emitted = output.Emit(image);
    if (!emitted.Success) {
      throw new InvalidOperationException(
        "The probe does not compile: "
        + string.Join(Environment.NewLine, emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
    }
    var assembly = System.Reflection.Assembly.Load(image.ToArray());
    var run = assembly.GetType("App.Probe", throwOnError: true)!.GetMethod("Run")!;
    return (string)run.Invoke(null, null)!;
  }

  /// <summary>
  /// The probe body for a <paramref name="type"/> whose own <c>ThingId</c> is its strong-id stream id and whose base
  /// declares a Guid <c>StreamId</c>: read the generation policy, set the stream id, and report both properties.
  /// </summary>
  private static string _setStrongIdProbe(string generatedNamespace, string type) => $$"""
    IStreamIdExtractor extractor = new global::{{generatedNamespace}}.Generated.GeneratedStreamIdExtractor();
    var message = new {{type}}();
    var policy = extractor.GetGenerationPolicy(message);
    var wrote = extractor.SetStreamId(message, new Guid("{{_mintedId}}"));
    return $"generate={policy.ShouldGenerate};set={wrote};own={message.ThingId.ToGuid()};base={message.StreamId}";
    """;

  private static string _expectedStrongIdWritten() =>
    $"generate=True;set=True;own={_mintedId};base={Guid.Empty}";

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

  [Test]
  [RequiresAssemblyFiles]
  public async Task ACommandWhoseStreamIdIsAStrongId_SetStreamIdWritesTheStrongIdNotTheInheritedGuidAsync() {
    const string contracts = """
      namespace App.Contracts {
        [WhizbangId]
        public readonly partial struct ThingId;

        public abstract class BaseCommand : ICommand { [StreamId] public virtual Guid StreamId { get; set; } }

        [GenerateStreamId]
        public class CreateThing : BaseCommand { [StreamId] public ThingId ThingId { get; set; } }
      }
      """;

    var observed = _runProbe("StrongIdCommand", contracts, _setStrongIdProbe("StrongIdCommand", "App.Contracts.CreateThing"));

    await Assert.That(observed).IsEqualTo(_expectedStrongIdWritten())
      .Because("the command routes by its strong id, so the minted id belongs there and not in the base's Guid");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AnEventWhoseStreamIdIsAStrongId_SetStreamIdWritesTheStrongIdNotTheInheritedGuidAsync() {
    const string contracts = """
      namespace App.Contracts {
        [WhizbangId]
        public readonly partial struct ThingId;

        public abstract class BaseEvent : IEvent { [StreamId] public virtual Guid StreamId { get; set; } }

        [GenerateStreamId]
        public class ThingCreated : BaseEvent { [StreamId] public ThingId ThingId { get; set; } }
      }
      """;

    var observed = _runProbe("StrongIdEvent", contracts, _setStrongIdProbe("StrongIdEvent", "App.Contracts.ThingCreated"));

    await Assert.That(observed).IsEqualTo(_expectedStrongIdWritten())
      .Because("the event routes by its strong id, so the minted id belongs there and not in the base's Guid");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AGenericBaseClosedWithAStrongId_GeneratesSetsAndRoutesByTheStrongIdAsync() {
    const string contracts = """
      namespace App.Contracts {
        [WhizbangId]
        public readonly partial struct ThingId;

        public abstract class BaseCommand<TStreamId> : ICommand where TStreamId : struct {
          [StreamId] [GenerateStreamId]
          public TStreamId StreamId { get; set; }
        }

        public class CreateThing : BaseCommand<ThingId>;
      }
      """;
    const string probe = """
      IStreamIdExtractor extractor = new global::StrongIdGenericBase.Generated.GeneratedStreamIdExtractor();
      var message = new App.Contracts.CreateThing();
      var policy = extractor.GetGenerationPolicy(message);
      var wrote = extractor.SetStreamId(message, new Guid("__MINTED__"));
      var routed = extractor.ExtractStreamId(message, typeof(App.Contracts.CreateThing));
      return $"generate={policy.ShouldGenerate};set={wrote};own={message.StreamId.ToGuid()};routed={routed}";
      """;

    var observed = _runProbe("StrongIdGenericBase", contracts, probe.Replace("__MINTED__", _mintedId.ToString()));

    await Assert.That(observed).IsEqualTo($"generate=True;set=True;own={_mintedId};routed={_mintedId}")
      .Because("the closed command inherits the base's strong-id stream id and its [GenerateStreamId]");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task GenerateStreamIdOnAnInitOnlyStrongId_ReportsWhiz013Async() {
    const string source = """
      using System;
      using Whizbang.Core;

      namespace App.Contracts;

      [WhizbangId]
      public readonly partial struct ThingId;

      [GenerateStreamId]
      public class CreateThing : ICommand { [StreamId] public ThingId ThingId { get; init; } }

      [GenerateStreamId]
      public class ThingCreated : IEvent { [StreamId] public ThingId ThingId { get; init; } }
      """;

    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);
    var code = GeneratorTestHelper.GetRequiredGeneratedSource(result, EXTRACTORS);

    await Assert.That(result.Diagnostics.Count(d => d.Id == "WHIZ013")).IsEqualTo(2)
      .Because("the generated setter cannot write an init-only strong id either, so the minted id would be lost");
    await Assert.That(code).DoesNotContain("new global::App.Contracts.ThingId(streamId)");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AStrongIdStreamIdInheritedFromASourceMessage_IsWrittenWhateverItsVersionAsync() {
    // The dispatcher also writes a stream id a cascaded message inherits from the message that caused it, and that id
    // need not be version 7; the setter writes it rather than failing the dispatch.
    const string contracts = """
      namespace App.Contracts {
        [WhizbangId]
        public readonly partial struct ThingId;

        public class ThingRenamed : IEvent { [StreamId] public ThingId ThingId { get; set; } }
      }
      """;
    const string probe = """
      var message = new App.Contracts.ThingRenamed();
      var wrote = global::StrongIdInherited.Generated.StreamIdExtractors.SetStreamId(message, new Guid("__INHERITED__"));
      return $"set={wrote};own={message.ThingId.ToGuid()}";
      """;
    var inherited = Guid.Parse("6f9619ff-8b86-4011-b42d-00c04fc964ff");

    var observed = _runProbe("StrongIdInherited", contracts, probe.Replace("__INHERITED__", inherited.ToString()));

    await Assert.That(observed).IsEqualTo($"set=True;own={inherited}")
      .Because("a version 4 stream id inherited from a source message is still the stream the message belongs to");
  }
}
