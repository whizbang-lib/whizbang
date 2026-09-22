using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The compile-time message-kind ladder, exercised through the generated
/// <c>HandledMessageInfo</c> entries that carry its verdict.
/// </summary>
/// <remarks>
/// The kind decides how the receive boundary treats a message — a command has one owner, an event
/// broadcasts, a query is answered — so a misclassification is a routing change, not a labeling
/// one. The ladder is five ordered rules, and the order is the interesting part: each level exists
/// because the one below it gets a real case wrong. The explicit attribute has to beat everything
/// (it is the escape hatch); the framework's own system namespace has to beat marker interfaces
/// (its commands implement ICommand but are run-control broadcast traffic); interfaces have to
/// beat namespace convention; and convention has to beat a name suffix.
///
/// <para>
/// This classifier is deliberately mirrored by the WHIZ151 ownership analyzer, so a silent change
/// here changes which code an analyzer reports on too.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Generators/CompileTimeMessageClassification.cs</code-under-test>
[Category("SourceGenerators")]
[Category("MessageKind")]
public class MessageKindClassificationTests {

  /// <summary>
  /// Runs the query generator over a receptor for <paramref name="messageDeclaration"/> and
  /// returns the MessageKind the generated registration recorded for it.
  /// </summary>
  private static string _kindOf(string messageDeclaration, string messageType) {
    var source = $@"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

{messageDeclaration}

namespace Consumers;

public class TheReceptor : Whizbang.Core.IReceptor<{messageType}> {{
  public ValueTask HandleAsync({messageType} message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}}";

    var result = GeneratorTestHelper.RunGenerator<ReceptorRegistryQueryGenerator>(source);
    var generated = GeneratorTestHelper.GetGeneratedSource(
      result, "WhizbangReceptorRegistryQueryRegistration.g.cs") ?? string.Empty;

    // Each entry is: new HandledMessageInfo("Type", "namespace", MessageKind.Kind),
    var anchor = generated.IndexOf($"\"{messageType}\"", StringComparison.Ordinal);
    if (anchor < 0) {
      return "(not registered)";
    }
    const string MARKER = "MessageKind.";
    var kindStart = generated.IndexOf(MARKER, anchor, StringComparison.Ordinal);
    if (kindStart < 0) {
      return "(no kind)";
    }
    kindStart += MARKER.Length;
    var end = kindStart;
    while (end < generated.Length && (char.IsLetterOrDigit(generated[end]) || generated[end] == '_')) {
      end++;
    }
    return generated[kindStart..end];
  }

  // ============================================================
  // Priority 1 — the explicit attribute
  // ============================================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ExplicitMessageKind_OutranksEveryOtherSignalAsync() {
    // The attribute is the escape hatch for a type the ladder would otherwise get wrong. It has
    // to win against a marker interface, a conventional namespace, AND a name suffix at once,
    // or it is not an escape hatch.
    var kind = _kindOf(@"
using Whizbang.Core.Routing;

namespace Shop.Commands;

[MessageKind(MessageKind.Event)]
public record ArchiveOrderCommand : ICommand { public string Id { get; init; } = string.Empty; }
", "Shop.Commands.ArchiveOrderCommand");

    await Assert.That(kind).IsEqualTo("Event")
      .Because("ICommand, a Commands namespace and a Command suffix all point the other way — "
             + "the attribute exists precisely to override them");
  }

  // ============================================================
  // Priority 2 — the framework's system namespace
  // ============================================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task FrameworkSystemNamespace_OutranksTheCommandInterfaceAsync() {
    // Framework system commands implement ICommand but are broadcast run-control traffic. If
    // the interface won here they would route as owned commands and only one instance in the
    // fleet would act on them.
    var kind = _kindOf(@"
namespace Whizbang.Core.Commands.System;

public record PauseIntakeCommand : Whizbang.Core.ICommand { public string Id { get; init; } = string.Empty; }
", "Whizbang.Core.Commands.System.PauseIntakeCommand");

    await Assert.That(kind).IsEqualTo("System");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task FrameworkSystemNamespace_CoversNestedNamespacesAsync() {
    var kind = _kindOf(@"
namespace Whizbang.Core.Commands.System.Runtime;

public record DrainCommand : Whizbang.Core.ICommand { public string Id { get; init; } = string.Empty; }
", "Whizbang.Core.Commands.System.Runtime.DrainCommand");

    await Assert.That(kind).IsEqualTo("System")
      .Because("the rule is the whole subtree — a nested namespace is still framework traffic");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ANamespaceThatMerelySharesThePrefix_IsNotSystemAsync() {
    // The guard is prefix-plus-dot rather than plain StartsWith, so a consumer namespace that
    // happens to begin with the same characters is not swept into framework traffic.
    var kind = _kindOf(@"
namespace Whizbang.Core.Commands.SystemsIntegration;

public record SyncCommand : Whizbang.Core.ICommand { public string Id { get; init; } = string.Empty; }
", "Whizbang.Core.Commands.SystemsIntegration.SyncCommand");

    await Assert.That(kind).IsEqualTo("Command");
  }

  // ============================================================
  // Priority 3 — marker interfaces
  // ============================================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task CommandInterface_ClassifiesAsCommandAsync() {
    var kind = _kindOf(@"
namespace Shop;

public record PlaceOrder : Whizbang.Core.ICommand { public string Id { get; init; } = string.Empty; }
", "Shop.PlaceOrder");

    await Assert.That(kind).IsEqualTo("Command");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task EventInterface_ClassifiesAsEventAsync() {
    var kind = _kindOf(@"
namespace Shop;

public record OrderPlaced : Whizbang.Core.IEvent { public string Id { get; init; } = string.Empty; }
", "Shop.OrderPlaced");

    await Assert.That(kind).IsEqualTo("Event");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Interface_OutranksAContradictingNamespaceAsync() {
    // An event living under a Commands namespace is ordinary in a contract assembly organized
    // by feature rather than by kind. The interface is the stronger statement.
    var kind = _kindOf(@"
namespace Shop.Commands;

public record OrderPlaced : Whizbang.Core.IEvent { public string Id { get; init; } = string.Empty; }
", "Shop.Commands.OrderPlaced");

    await Assert.That(kind).IsEqualTo("Event");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Interface_OutranksAContradictingNameSuffixAsync() {
    var kind = _kindOf(@"
namespace Shop;

public record ArchiveCommand : Whizbang.Core.IEvent { public string Id { get; init; } = string.Empty; }
", "Shop.ArchiveCommand");

    await Assert.That(kind).IsEqualTo("Event");
  }

  // ============================================================
  // Priority 4 — namespace convention
  // ============================================================

  [Test]
  [RequiresAssemblyFiles()]
  [Arguments("Shop.Commands", "Command")]
  [Arguments("Shop.Events", "Event")]
  [Arguments("Shop.Queries", "Query")]
  public async Task NamespaceConvention_ClassifiesAnUnmarkedTypeAsync(string ns, string expected) {
    // A contract type with no marker interface is common in an assembly shared with
    // non-Whizbang consumers, and the folder it lives in is the next best signal.
    var kind = _kindOf($@"
namespace {ns};

public record Thing {{ public string Id {{ get; init; }} = string.Empty; }}
", $"{ns}.Thing");

    await Assert.That(kind).IsEqualTo(expected);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task NamespaceConvention_IgnoresSegmentCaseAsync() {
    var kind = _kindOf(@"
namespace Shop.EVENTS;

public record Thing { public string Id { get; init; } = string.Empty; }
", "Shop.EVENTS.Thing");

    await Assert.That(kind).IsEqualTo("Event");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task NamespaceConvention_MatchesAnySegmentNotJustTheLastAsync() {
    var kind = _kindOf(@"
namespace Shop.Events.Fulfillment;

public record Thing { public string Id { get; init; } = string.Empty; }
", "Shop.Events.Fulfillment.Thing");

    await Assert.That(kind).IsEqualTo("Event");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task NamespaceConvention_OutranksAContradictingNameSuffixAsync() {
    var kind = _kindOf(@"
namespace Shop.Events;

public record ArchiveCommand { public string Id { get; init; } = string.Empty; }
", "Shop.Events.ArchiveCommand");

    await Assert.That(kind).IsEqualTo("Event")
      .Because("the folder a contract lives in is a deliberate choice; a suffix is often habit");
  }

  // ============================================================
  // Priority 5 — the type-name suffix
  // ============================================================

  [Test]
  [RequiresAssemblyFiles()]
  [Arguments("PlaceOrderCommand", "Command")]
  [Arguments("OrderByIdQuery", "Query")]
  [Arguments("OrderShippedEvent", "Event")]
  [Arguments("OrderCreated", "Event")]
  [Arguments("OrderUpdated", "Event")]
  [Arguments("OrderDeleted", "Event")]
  public async Task NameSuffix_IsTheLastResortAsync(string typeName, string expected) {
    // Past-tense verbs are the common shape for events even when nobody wrote "Event", so
    // Created/Updated/Deleted are treated as suffixes in their own right.
    var kind = _kindOf($@"
namespace Shop;

public record {typeName} {{ public string Id {{ get; init; }} = string.Empty; }}
", $"Shop.{typeName}");

    await Assert.That(kind).IsEqualTo(expected);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task NameSuffix_IsCaseSensitiveAsync() {
    // Matched with Ordinal comparison, so a lowercased suffix does not count — worth pinning
    // because it is the difference between Unknown and Command for a sloppily named type.
    var kind = _kindOf(@"
namespace Shop;

public record Placeordercommand { public string Id { get; init; } = string.Empty; }
", "Shop.Placeordercommand");

    await Assert.That(kind).IsEqualTo("Unknown");
  }

  // ============================================================
  // The floor
  // ============================================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task NoSignalAtAll_IsUnknownAsync() {
    // Unknown is a real answer, not a failure: it tells the receive boundary it has no basis to
    // pick a routing shape, which is safer than guessing one.
    var kind = _kindOf(@"
namespace Shop.Contracts;

public record Payload { public string Id { get; init; } = string.Empty; }
", "Shop.Contracts.Payload");

    await Assert.That(kind).IsEqualTo("Unknown");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task TheGeneratorEmitsNoErrorsForAnyOfTheseShapesAsync() {
    var result = GeneratorTestHelper.RunGenerator<ReceptorRegistryQueryGenerator>(@"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;

namespace Shop.Commands;
public record PlaceOrder : ICommand { public string Id { get; init; } = string.Empty; }

namespace Consumers;
public class R : Whizbang.Core.IReceptor<Shop.Commands.PlaceOrder> {
  public ValueTask HandleAsync(Shop.Commands.PlaceOrder message, CancellationToken ct = default)
    => ValueTask.CompletedTask;
}");

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
  }
}
