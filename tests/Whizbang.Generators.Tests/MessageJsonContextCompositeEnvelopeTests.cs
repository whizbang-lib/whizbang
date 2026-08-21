using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests that <see cref="MessageJsonContextGenerator"/> emits <c>JsonTypeInfo</c> for
/// <c>MessageEnvelope&lt;TComposite&gt;</c> when a consumer declares a composite event.
///
/// <para>A composite is wire-only — it implements <c>IMessage</c>, never <c>IEvent</c> — so nothing
/// registers a consumer for the composite type itself; its consumers are the inner events, which
/// only become addressable after the dispatch seam expands it. That makes the envelope's
/// <c>JsonTypeInfo</c> load-bearing: without it the receiver cannot bind the envelope at all, the
/// message fails deserialization on every delivery, and it burns its retry budget until it is
/// dead-lettered — never once reaching the fan-out that would have made it useful.</para>
///
/// <para>Observed in production as a broker dead-letter storm:
/// <c>MaxDeliveryAttemptsExceeded — JsonTypeInfo metadata for type
/// 'Whizbang.Core.Observability.MessageEnvelope`1[Consumer.Job.DraftJobEventsComposite]' was not
/// provided by TypeInfoResolver</c>, alongside inbox rows dying at <c>attempts &gt; max=10</c>.</para>
/// </summary>
[Category("SourceGenerators")]
[Category("JsonSerialization")]
public class MessageJsonContextCompositeEnvelopeTests {

  /// <summary>
  /// The shape a consumer actually writes: a concrete sealed class whose ONLY composite marker is
  /// the abstract base it inherits. The base is abstract, so the generator skips it as a wire type —
  /// the subclass has to be recognised as a composite through <c>AllInterfaces</c>, not through its
  /// own base list naming the interface.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_CompositeInheritingBase_EmitsEnvelopeJsonTypeInfoAsync() {
    var source = @"
using Whizbang.Core.Minting;

namespace ConsumerApp.Job;

public sealed class DraftJobEventsComposite : CompositeEventBase {
  public DraftJobEventsComposite() {
    Atomicity = FanoutAtomicity.Atomic;
  }
}
";

    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    await Assert.That(code!).Contains("MessageEnvelope<global::ConsumerApp.Job.DraftJobEventsComposite>")
      .Because("a composite that inherits CompositeEventBase is still a wire type; without envelope "
             + "JsonTypeInfo the receiver cannot bind it, so it fails every delivery and dead-letters "
             + "before the dispatch seam can expand it");
  }

  /// <summary>
  /// Control: the same assertion for a composite that names the interface directly. If this passes
  /// while the inherited-base case above fails, discovery — not envelope emission — is the gap.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_CompositeImplementingInterfaceDirectly_EmitsEnvelopeJsonTypeInfoAsync() {
    var source = @"
using System.Collections.Generic;
using Whizbang.Core;
using Whizbang.Core.Minting;

namespace ConsumerApp.Job;

public sealed class DirectComposite : ICompositeEvent {
  public System.Guid MessageId { get; set; }
  public System.DateTimeOffset OccurredAt { get; set; }
  public System.Guid? CorrelationId { get; set; }
  public System.Guid? CausationId { get; set; }
  public string? OperationName { get; set; }
  public IEnumerable<IMessage> InnerEvents => new List<IMessage>();
  public int MaxInnerEventsAllowed => 10;
  public FanoutMode FanoutMode => FanoutMode.Auto;
  public FanoutAtomicity Atomicity => FanoutAtomicity.Independent;
}
";

    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    await Assert.That(code!).Contains("MessageEnvelope<global::ConsumerApp.Job.DirectComposite>")
      .Because("a directly-declared composite must get envelope JsonTypeInfo for the same reason");
  }
}
