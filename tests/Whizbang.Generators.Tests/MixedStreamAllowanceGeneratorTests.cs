// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// #2255: <c>[DangerouslyAllowMixedEphemeralAndSourcedEvents(Streams = MixedStreams.Allow)]</c>. The generated registry
/// carries each read model's (ephemeral, Sourced) pairs and whether it allows them to share a stream, and the ephemeral
/// analyzer reports, as WHIZ139, a read model that applies an allowed pair without the opt-in (which keeps it refused).
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#allowing-mixed-streams</docs>
/// <code-under-test>src/Whizbang.Generators/PerspectiveRunnerRegistryGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/EphemeralAnalyzer.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/Utilities/MixedStreamOptIn.cs</code-under-test>
[Category("Analyzers")]
public class MixedStreamAllowanceGeneratorTests {
  private const string REGISTRY_SOURCE = """
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  [Ephemeral]
  public record BatchGenerated : IEvent {
    [StreamId]
    public string BatchId { get; init; } = "";
  }

  public record ItemGenerated : IEvent {
    [StreamId]
    public string BatchId { get; init; } = "";
  }

  public record BatchModel {
    [StreamId]
    public string BatchId { get; init; } = "";
  }

  [DangerouslyAllowMixedEphemeralAndSourcedEvents(Streams = MixedStreams.Allow)]
  public class AllowingPerspective : IPerspectiveFor<BatchModel, BatchGenerated, ItemGenerated> {
    public BatchModel Apply(BatchModel currentData, BatchGenerated @event) => currentData;
    public BatchModel Apply(BatchModel currentData, ItemGenerated @event) => currentData;
  }

  [DangerouslyAllowMixedEphemeralAndSourcedEvents]
  public class DefaultPerspective : IPerspectiveFor<BatchModel, BatchGenerated, ItemGenerated> {
    public BatchModel Apply(BatchModel currentData, BatchGenerated @event) => currentData;
    public BatchModel Apply(BatchModel currentData, ItemGenerated @event) => currentData;
  }

  public class UnmarkedPerspective : IPerspectiveFor<BatchModel, BatchGenerated>, IPerspectiveFor<BatchModel, ItemGenerated> {
    public BatchModel Apply(BatchModel currentData, BatchGenerated @event) => currentData;
    public BatchModel Apply(BatchModel currentData, ItemGenerated @event) => currentData;
  }

  public class PlainPerspective : IPerspectiveFor<BatchModel, ItemGenerated> {
    public BatchModel Apply(BatchModel currentData, ItemGenerated @event) => currentData;
  }
}
""";

  private const string PAIR = "new global::Whizbang.Core.Perspectives.MixedStreamPair(\"TestNamespace.BatchGenerated, TestAssembly\", \"TestNamespace.ItemGenerated, TestAssembly\")";

  private const string HEADER = """
    using Whizbang.Core;
    using Whizbang.Core.Attributes;
    using Whizbang.Core.Perspectives;
    namespace TestApp;
    public class Model { public System.Guid Id { get; set; } }
    [Ephemeral]
    public record PresencePing : IEvent;
    public record OrderPlaced : IEvent;
    public record OrderShipped : IEvent;
    [DangerouslyAllowMixedEphemeralAndSourcedEvents(Streams = MixedStreams.Allow)]
    public class PresenceBoard
      : IPerspectiveFor<Model, PresencePing>, IPerspectiveFor<Model, OrderPlaced>, IPerspectiveFor<Model, OrderShipped> {
      public Model Apply(Model current, PresencePing e) => current;
      public Model Apply(Model current, OrderPlaced e) => current;
      public Model Apply(Model current, OrderShipped e) => current;
    }

    """;

  [Test]
  [RequiresAssemblyFiles]
  public async Task Registry_AnAllowingReadModel_CarriesItsPairs_AndTheDefaultCarriesNoneAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerRegistryGenerator>(REGISTRY_SOURCE);

    var registry = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRunnerRegistry.g.cs");
    await Assert.That(registry).IsNotNull();
    var allowing = _entry(registry!, "TestNamespace.AllowingPerspective");
    var defaulted = _entry(registry!, "TestNamespace.DefaultPerspective");
    var plain = _entry(registry!, "TestNamespace.PlainPerspective");
    await Assert.That(allowing).Contains("MixedStreams = global::Whizbang.Core.Attributes.MixedStreams.Allow")
      .Because("the runtime allows only the pairs of a read model that set Streams = MixedStreams.Allow");
    await Assert.That(allowing).Contains($"MixedStreamPairs = [{PAIR}]");
    await Assert.That(defaulted).DoesNotContain("MixedStreams =")
      .Because("the attribute without the property is Refuse, which is the runtime as before");
    await Assert.That(plain).DoesNotContain("MixedStream").Because("a read model applying one kind has no pair");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Registry_AReadModelWithoutTheOptIn_CarriesItsPairs_SoTheyStayRefusedAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerRegistryGenerator>(REGISTRY_SOURCE);

    var registry = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRunnerRegistry.g.cs")!;
    await Assert.That(_entry(registry, "TestNamespace.DefaultPerspective")).Contains($"MixedStreamPairs = [{PAIR}]")
      .Because("a read model that applies both without Allow keeps the pair refused for every read model, so the runtime must see it");
    await Assert.That(_entry(registry, "TestNamespace.UnmarkedPerspective")).Contains($"MixedStreamPairs = [{PAIR}]")
      .Because("pairs spread over several interfaces are the same pairs");
    await Assert.That(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task SecondReadModelWithoutAllow_ReportsWHIZ139OnItAsync() {
    const string source = HEADER + """
      [DangerouslyAllowMixedEphemeralAndSourcedEvents]
      public class OrderTimeline : IPerspectiveFor<Model, PresencePing>, IPerspectiveFor<Model, OrderPlaced> {
        public Model Apply(Model current, PresencePing e) => current;
        public Model Apply(Model current, OrderPlaced e) => current;
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<EphemeralAnalyzer>(source);

    var found = diagnostics.Where(d => d.Id == "WHIZ139").ToArray();
    await Assert.That(found.Length).IsEqualTo(1).Because("OrderTimeline keeps (PresencePing, OrderPlaced) refused; OrderShipped is not its");
    await Assert.That(found[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
    var message = found[0].GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("'OrderTimeline'");
    await Assert.That(message).Contains("TestApp.PresencePing");
    await Assert.That(message).Contains("TestApp.OrderPlaced");
    await Assert.That(message).Contains("'PresenceBoard' allows them");
    await Assert.That(found[0].Location.SourceTree).IsNotNull().Because("the diagnostic points at the read model to change");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task EveryReadModelAllows_OrNoneDoes_NoWHIZ139Async() {
    const string allBoth = HEADER + """
      [DangerouslyAllowMixedEphemeralAndSourcedEvents(Streams = MixedStreams.Allow)]
      public class OrderTimeline : IPerspectiveFor<Model, PresencePing>, IPerspectiveFor<Model, OrderPlaced> {
        public Model Apply(Model current, PresencePing e) => current;
        public Model Apply(Model current, OrderPlaced e) => current;
      }
      public class OrderList : IPerspectiveFor<Model, OrderPlaced> {
        public Model Apply(Model current, OrderPlaced e) => current;
      }
      """;
    const string noneAllow = """
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      using Whizbang.Core.Perspectives;
      namespace TestApp;
      public class Model { public System.Guid Id { get; set; } }
      [Ephemeral]
      public record PresencePing : IEvent;
      public record OrderPlaced : IEvent;
      [DangerouslyAllowMixedEphemeralAndSourcedEvents(Streams = MixedStreams.Refuse)]
      public class OrderTimeline : IPerspectiveFor<Model, PresencePing>, IPerspectiveFor<Model, OrderPlaced> {
        public Model Apply(Model current, PresencePing e) => current;
        public Model Apply(Model current, OrderPlaced e) => current;
      }
      public class Unmarked : IPerspectiveFor<Model, PresencePing>, IPerspectiveFor<Model, OrderPlaced> {
        public Model Apply(Model current, PresencePing e) => current;
        public Model Apply(Model current, OrderPlaced e) => current;
      }
      """;

    var all = await AnalyzerTestHelper.GetDiagnosticsAsync<EphemeralAnalyzer>(allBoth);
    var none = await AnalyzerTestHelper.GetDiagnosticsAsync<EphemeralAnalyzer>(noneAllow);

    await Assert.That(all.Any(d => d.Id == "WHIZ139")).IsFalse()
      .Because("every read model applying both opts in, and one applying only the Sourced type is no concern");
    await Assert.That(none.Any(d => d.Id == "WHIZ139")).IsFalse().Because("with nothing allowed there is nothing kept refused");
    await Assert.That(none.Count(d => d.Id == "WHIZ130")).IsEqualTo(1)
      .Because("control: Streams = Refuse still silences WHIZ130, and the unmarked read model still gets it");
  }

  // The generated registration of one perspective: from its CLR name to the next registration or the array's end.
  private static string _entry(string registry, string clrTypeName) {
    var start = registry.IndexOf($"\"{clrTypeName}\",", StringComparison.Ordinal);
    var next = registry.IndexOf("new PerspectiveRegistrationInfo(", start, StringComparison.Ordinal);
    return registry[start..(next < 0 ? registry.IndexOf("];", start, StringComparison.Ordinal) : next)];
  }
}
