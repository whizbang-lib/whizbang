using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused tests for StreamIdGenerator targeting the command pipeline, the
/// constructor-parameter [StreamId] discovery path, composite exclusions, and the
/// try-extractor snippet selection for string / value-type / other property types.
/// Complements StreamIdGeneratorTests.cs and GenerateStreamIdGeneratorTests.cs.
/// </summary>
public class StreamIdGeneratorCoverageTests {
  // --- Non-public type skips (all four discovery pipelines) ---

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_InternalEventCommandAndComposite_SkipsAllPipelinesAsync() {
    // Internal types can't be referenced from the generated (public) extractor class, so every
    // pipeline (event, event-without-id, command, composite) must skip them.
    const string source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Messaging;

            namespace TestNamespace;

            internal sealed class InternalOrderEvent : IEvent {
              [StreamId]
              public Guid OrderId { get; set; }
            }

            internal sealed class InternalAssignCommand : ICommand {
              [StreamId]
              public Guid OrderId { get; set; }
            }

            internal sealed class InternalComposite : CompositeEventBase { }
            """;

    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated!).DoesNotContain("InternalOrderEvent");
    await Assert.That(generated!).DoesNotContain("InternalAssignCommand");
    await Assert.That(generated!).DoesNotContain("InternalComposite");
    // No discovery diagnostics and no missing-StreamId warning for skipped internal types.
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Id == "WHIZ010");
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Id == "WHIZ004");
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Id == "WHIZ009");
  }

  // --- Composite exclusions and constructor-parameter composites ---

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_CompositeAlsoImplementingIEventOrICommand_ExcludedFromObjectResolverAsync() {
    // A composite that ALSO implements IEvent (or ICommand) is handled by the event/command
    // pipelines; the composite pipeline must skip it to avoid duplicate extractor overloads.
    const string source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Messaging;

            namespace TestNamespace;

            public sealed class EventishComposite : CompositeEventBase, IEvent { }

            public sealed class CommandishComposite : CompositeEventBase, ICommand { }
            """;

    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generated).IsNotNull();
    // Handled by the IEvent / ICommand dispatch paths...
    await Assert.That(generated!).Contains("@event is global::TestNamespace.EventishComposite");
    await Assert.That(generated!).Contains("command is global::TestNamespace.CommandishComposite");
    // ...and NOT by the object-typed composite dispatch (its cases use `message is <type>`).
    await Assert.That(generated!).DoesNotContain("message is global::TestNamespace.EventishComposite");
    await Assert.That(generated!).DoesNotContain("message is global::TestNamespace.CommandishComposite");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_CompositeRecordWithStreamIdOnParameter_GeneratesObjectResolverExtractorAsync() {
    // A record composite carrying [StreamId] on a positional constructor parameter (not on a
    // property) must be discovered via the constructor-parameter fallback of the composite pipeline.
    const string source = """
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using Whizbang.Core;
            using Whizbang.Core.Messaging;

            namespace TestNamespace;

            public sealed record BulkRouteComposite([StreamId] Guid RouteId) : ICompositeEvent {
              public IEnumerable<IMessage> InnerEvents => Enumerable.Empty<IMessage>();
            }
            """;

    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated!).Contains("message is global::TestNamespace.BulkRouteComposite");
    await Assert.That(generated!).Contains("RouteId");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_TwoComposites_GeneratesBothObjectResolverExtractorsAsync() {
    // Two concrete composites exercise the multi-composite loop (separator between extractors).
    const string source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Messaging;

            namespace TestNamespace;

            public sealed class FirstComposite : CompositeEventBase { }

            public sealed class SecondComposite : CompositeEventBase { }
            """;

    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated!).Contains("message is global::TestNamespace.FirstComposite");
    await Assert.That(generated!).Contains("message is global::TestNamespace.SecondComposite");
    // Both dispatch cases get distinct pattern variables.
    await Assert.That(generated!).Contains("TryExtractAsGuid(o0)");
    await Assert.That(generated!).Contains("TryExtractAsGuid(o1)");
  }

  // --- [StreamId] on constructor parameters (events) ---

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_EventRecordWithStreamIdOnParameter_GeneratesExtractorAsync() {
    // [StreamId] without a `property:` target lands on the positional parameter, exercising the
    // constructor-parameter discovery path (and suppressing the WHIZ009 missing-StreamId warning).
    const string source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record ParamOrderPlaced([StreamId] Guid OrderId, string ProductName) : IEvent;
            """;

    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated!).Contains("ParamOrderPlaced");
    await Assert.That(generated!).Contains("OrderId");

    var discovered = result.Diagnostics.Where(d => d.Id == "WHIZ010").ToArray();
    await Assert.That(discovered).Count().IsEqualTo(1);
    // The parameter-level [StreamId] must be recognized, so no missing-StreamId warning.
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Id == "WHIZ009");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_EventParameterWithGenerateStreamIdOnlyIfEmpty_GeneratesPolicyAsync() {
    // [GenerateStreamId(OnlyIfEmpty = true)] on the parameter itself exercises the
    // parameter-attribute OnlyIfEmpty extraction path.
    const string source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record ParamArchive([StreamId] [GenerateStreamId(OnlyIfEmpty = true)] Guid StreamId) : IEvent;
            """;

    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated!).Contains("GetGenerationPolicy");
    await Assert.That(generated!).Contains("message is global::TestNamespace.ParamArchive");
    await Assert.That(generated!).Contains("(true, true)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_EventParameterWithClassLevelGenerateStreamId_GeneratesPolicyAsync() {
    // [GenerateStreamId] on the record itself combined with a parameter-level [StreamId]
    // exercises the class-attribute fallback of the parameter resolution path.
    const string source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            [GenerateStreamId]
            public record ParamClassGen([StreamId] Guid StreamId) : IEvent;
            """;

    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated!).Contains("message is global::TestNamespace.ParamClassGen");
    await Assert.That(generated!).Contains("(true, false)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_CtorParameterWithoutMatchingProperty_SkipsTypeAsync() {
    // A plain class constructor parameter with [StreamId] but no same-named property has nothing
    // to read from at extraction time — the parameter is skipped (continue) and no extractor emitted.
    // The without-StreamId pipeline still sees the parameter attribute, so no WHIZ009 either.
    const string source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public class CtorParamEvent : IEvent {
              public CtorParamEvent([StreamId] Guid orderId) {
                OtherId = orderId;
              }
              public Guid OtherId { get; set; }
            }

            public class CtorParamCommand : ICommand {
              public CtorParamCommand([StreamId] Guid orderId) {
                OtherId = orderId;
              }
              public Guid OtherId { get; set; }
            }
            """;

    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated!).DoesNotContain("CtorParamEvent");
    await Assert.That(generated!).DoesNotContain("CtorParamCommand");
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Id == "WHIZ010");
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Id == "WHIZ004");
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Id == "WHIZ009");
  }

  // --- Command pipeline ---

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_CommandWithGuidStreamIdProperty_GeneratesCommandExtractorsAsync() {
    const string source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public class AssignOrderCommand : ICommand {
              [StreamId]
              public Guid OrderId { get; set; }
            }
            """;

    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    // WHIZ004 (info): [StreamId] discovered on a command.
    var discovered = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ004");
    await Assert.That(discovered).IsNotNull();
    await Assert.That(discovered!.Severity).IsEqualTo(DiagnosticSeverity.Info);

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generated).IsNotNull();
    // Command dispatch + try-dispatch cases.
    await Assert.That(generated!).Contains("command is global::TestNamespace.AssignOrderCommand c0");
    await Assert.That(generated!).Contains("TryExtractAsGuidFromCommand(c0)");
    // Command extractor methods (non-nullable Guid → no null check).
    await Assert.That(generated!).Contains("public static string ExtractFromCommand(global::TestNamespace.AssignOrderCommand command)");
    await Assert.That(generated!).Contains("private static global::System.Guid? TryExtractAsGuidFromCommand(global::TestNamespace.AssignOrderCommand command)");
    // Mutable Guid [StreamId] → SetStreamId writer case is generated.
    await Assert.That(generated!).Contains("message is global::TestNamespace.AssignOrderCommand setC0");
    await Assert.That(generated!).Contains("setC0.OrderId = streamId;");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_CommandRecordWithStreamIdOnParameter_GeneratesExtractorAsync() {
    // Command counterpart of the constructor-parameter discovery path.
    const string source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public record RenameOrderCommand([StreamId] Guid OrderId, string NewName) : ICommand;
            """;

    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    var discovered = result.Diagnostics.Where(d => d.Id == "WHIZ004").ToArray();
    await Assert.That(discovered).Count().IsEqualTo(1);

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated!).Contains("command is global::TestNamespace.RenameOrderCommand c0");
    await Assert.That(generated!).Contains("public static string ExtractFromCommand(global::TestNamespace.RenameOrderCommand command)");
    await Assert.That(generated!).Contains("OrderId");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_CommandGenerateStreamIdOnInitOnlyProperty_ReportsWHIZ013Async() {
    // WHIZ013 must fire for commands too: a minted id can't be written back to an init-only property.
    const string source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public class SealOrderCommand : ICommand {
              [StreamId]
              [GenerateStreamId]
              public Guid OrderId { get; init; }
            }
            """;

    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    var error = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ013");
    await Assert.That(error).IsNotNull();
    await Assert.That(error!.Severity).IsEqualTo(DiagnosticSeverity.Error);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_CommandWithGenerateStreamId_GeneratesPolicyCaseAsync() {
    // Commands with [GenerateStreamId] must get their own GetGenerationPolicy dispatch case.
    const string source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public class OpenOrderCommand : ICommand {
              [StreamId]
              [GenerateStreamId(OnlyIfEmpty = true)]
              public Guid OrderId { get; set; }
            }
            """;

    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Id == "WHIZ013");

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated!).Contains("GetGenerationPolicy");
    await Assert.That(generated!).Contains("message is global::TestNamespace.OpenOrderCommand");
    await Assert.That(generated!).Contains("(true, true)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_CommandsWithVariousStreamIdTypes_SelectCorrectTryExtractorsAsync() {
    // One command per property-type category drives every branch of the command try-extractor
    // snippet selection: Guid? / string / non-Guid value type / nullable value type ("other").
    const string source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public class NullableGuidCommand : ICommand {
              [StreamId]
              public Guid? RefId { get; set; }
            }

            public class StringKeyCommand : ICommand {
              [StreamId]
              public string OrderKey { get; set; } = string.Empty;
            }

            public class IntKeyCommand : ICommand {
              [StreamId]
              public int Counter { get; set; }
            }

            public class NullableIntCommand : ICommand {
              [StreamId]
              public int? MaybeCounter { get; set; }
            }
            """;

    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generated).IsNotNull();
    // Guid? → NULLABLE_GUID snippet: direct return of the property.
    await Assert.That(generated!).Contains("return command.RefId;");
    // string → STRING snippet: null/whitespace check then Guid.TryParse on the raw string.
    await Assert.That(generated!).Contains("var key = command.OrderKey;");
    await Assert.That(generated!).Contains("global::System.Guid.TryParse(key, out var guid)");
    // int (non-Guid value type) → VALUE_TYPE snippet: ToString() directly on the property.
    await Assert.That(generated!).Contains("var keyString = command.Counter.ToString();");
    // int? (nullable value type) → OTHER snippet: null check then ToString() on the boxed key.
    await Assert.That(generated!).Contains("var key = command.MaybeCounter;");
    await Assert.That(generated!).Contains("var keyString = key.ToString();");
  }

  // --- Event try-extractor snippet selection (string / value type / other) ---

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_EventWithStringStreamId_UsesStringTryExtractorAsync() {
    const string source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public class NamedEvent : IEvent {
              [StreamId]
              public string OrderKey { get; set; } = string.Empty;
            }
            """;

    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generated).IsNotNull();
    // STRING snippet parses the raw string value.
    await Assert.That(generated!).Contains("var key = @event.OrderKey;");
    await Assert.That(generated!).Contains("global::System.Guid.TryParse(key, out var guid)");
    // string is nullable-ish → the throwing Extract uses the null/empty-checking variant.
    await Assert.That(generated!).Contains("Stream ID 'OrderKey' on NamedEvent cannot be empty.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_EventWithIntStreamId_UsesValueTypeTryExtractorAsync() {
    const string source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public class CounterEvent : IEvent {
              [StreamId]
              public int Counter { get; set; }
            }
            """;

    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generated).IsNotNull();
    // VALUE_TYPE snippet calls ToString() directly on the (non-nullable) struct property.
    await Assert.That(generated!).Contains("var keyString = @event.Counter.ToString();");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_EventWithNullableIntStreamId_UsesOtherTryExtractorAsync() {
    const string source = """
            using System;
            using Whizbang.Core;

            namespace TestNamespace;

            public class MaybeCounterEvent : IEvent {
              [StreamId]
              public int? Counter { get; set; }
            }
            """;

    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generated).IsNotNull();
    // OTHER snippet: null check, then ToString() on the captured key.
    await Assert.That(generated!).Contains("var key = @event.Counter;");
    await Assert.That(generated!).Contains("var keyString = key.ToString();");
  }

  // --- Generated code compiles ---

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_EventCommandAndComposite_GeneratedCodeCompilesAsync() {
    // End-to-end sanity: the emitted extractor class (event + command + composite regions all
    // populated) must compile against Whizbang.Core without errors.
    const string source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Messaging;

            namespace TestNamespace;

            public class OrderPlaced : IEvent {
              [StreamId]
              public Guid OrderId { get; set; }
            }

            public class AssignOrder : ICommand {
              [StreamId]
              public Guid OrderId { get; set; }
            }

            public sealed class OrderBulkComposite : CompositeEventBase { }
            """;

    var errors = GeneratorTestHelper.GetGeneratedCompilationErrors<StreamIdGenerator>(source);

    await Assert.That(errors).IsEmpty();
  }
}
