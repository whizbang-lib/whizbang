// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for <c>[StreamId(Override = true)]</c> on the generator side (#1833). An override is the stream id, ahead
/// of any inherited <c>[StreamId]</c>, including where an inherited property used to beat a derived record's
/// <c>[StreamId]</c> parameter. The analyzer side (WHIZ006 and WHIZ016) is locked in by
/// <c>StreamIdAttributeAnalyzerTests</c>, and both read the same choice.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/Utilities/StreamIdSelection.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/StreamIdGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/PerspectiveDiscoveryGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/PerspectiveRunnerGenerator.cs</code-under-test>
public class StreamIdOverrideGeneratorTests {
  private static string _discovered(GeneratorDriverRunResult result, string id, string typeName) =>
    result.Diagnostics
      .Where(d => d.Id == id)
      .Select(d => d.GetMessage(CultureInfo.InvariantCulture))
      .Single(m => m.Contains($" {typeName}.", StringComparison.Ordinal));

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AnEventOverrideProperty_IsTheStreamIdAheadOfTheInheritedOneAsync() {
    const string source = """
      using System;
      using Whizbang.Core;
      namespace TestNamespace;
      public abstract record OrderEventBase : IEvent { [StreamId] public Guid OrderId { get; init; } }
      public record LineAdded : OrderEventBase { [StreamId(Override = true)] public Guid LineId { get; init; } }
      """;

    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    await Assert.That(_discovered(result, "WHIZ010", "LineAdded")).EndsWith("LineAdded.LineId");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AnEventOverrideParameter_IsTheStreamIdAheadOfAnInheritedPropertyAsync() {
    const string source = """
      using System;
      using Whizbang.Core;
      namespace TestNamespace;
      public abstract record OrderEventBase : IEvent { [StreamId] public Guid OrderId { get; init; } }
      public record LineAdded([StreamId(Override = true)] Guid LineId) : OrderEventBase;
      """;

    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    await Assert.That(_discovered(result, "WHIZ010", "LineAdded")).EndsWith("LineAdded.LineId")
      .Because("without the option the inherited OrderId property is found first; an override parameter beats it");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AnOverrideOnAnIntermediateRecord_IsInheritedByTheLeafAsync() {
    const string source = """
      using System;
      using Whizbang.Core;
      namespace TestNamespace;
      public abstract record EventRoot : IEvent { [StreamId] public Guid TenantId { get; init; } }
      public abstract record OrderEventBase([StreamId(Override = true)] Guid OrderId) : EventRoot;
      public record LineAdded(Guid OrderId, string Sku) : OrderEventBase(OrderId);
      """;

    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    await Assert.That(_discovered(result, "WHIZ010", "LineAdded")).EndsWith("LineAdded.OrderId")
      .Because("the nearest override in the base chain is the stream id, two levels down as much as one");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AnInheritedOverrideWithNoOtherStreamId_IsNotReportedAsMissingAsync() {
    const string source = """
      using System;
      using Whizbang.Core;
      namespace TestNamespace;
      public abstract record OrderEventBase([StreamId(Override = true)] Guid OrderId) : IEvent;
      public record LineAdded(Guid OrderId, string Sku) : OrderEventBase(OrderId);
      """;

    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    await Assert.That(result.Diagnostics.Any(d => d.Id == "WHIZ009")).IsFalse()
      .Because("the inherited override parameter is a stream id, so the event is not missing one");
    await Assert.That(_discovered(result, "WHIZ010", "LineAdded")).EndsWith("LineAdded.OrderId");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ACommandOverrideParameter_IsTheStreamIdAheadOfAnInheritedPropertyAsync() {
    const string source = """
      using System;
      using Whizbang.Core;
      namespace TestNamespace;
      public abstract record CommandBase : ICommand { [StreamId] public virtual Guid StreamId { get; init; } }
      public record AcknowledgeAlert([StreamId(Override = true)] Guid AckId) : CommandBase;
      """;

    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    await Assert.That(_discovered(result, "WHIZ004", "AcknowledgeAlert")).EndsWith("AcknowledgeAlert.AckId");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ACompositeOverrideParameter_IsTheStreamIdAheadOfTheCompositeBaseAsync() {
    const string source = """
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Minting;
      namespace TestNamespace;
      public sealed class BulkImported([StreamId(Override = true)] Guid batchId) : CompositeEventBase {
        public Guid BatchId { get; } = batchId;
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs");
    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains("BatchId")
      .Because("the composite's override replaces the StreamId it inherits from CompositeEventBase");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task APerspectiveEventWithAnOverride_IsNotReportedAsMultipleStreamIdsAsync() {
    const string source = """
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      namespace TestNamespace {
        public abstract record OrderEventBase : IEvent { [StreamId] public Guid OrderId { get; init; } }
        public record LineAdded : OrderEventBase { [StreamId(Override = true)] public Guid LineId { get; init; } }
        public record LineModel { [StreamId] public Guid LineId { get; init; } }
        public class LinePerspective : IPerspectiveFor<LineModel, LineAdded> {
          public LineModel Apply(LineModel currentData, LineAdded @event) => currentData;
        }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    await Assert.That(result.Diagnostics.Any(d => d.Id is "WHIZ030" or "WHIZ031")).IsFalse()
      .Because("an override hides the inherited stream id on purpose, so the event has one stream id");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task APerspectiveEventWithAnOverrideParameter_IsValidAsync() {
    const string source = """
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      namespace TestNamespace {
        public abstract record OrderEventBase([StreamId(Override = true)] Guid OrderId) : IEvent;
        public record LineAdded(Guid OrderId, string Sku) : OrderEventBase(OrderId);
        public record LineModel { [StreamId] public Guid OrderId { get; init; } }
        public class LinePerspective : IPerspectiveFor<LineModel, LineAdded> {
          public LineModel Apply(LineModel currentData, LineAdded @event) => currentData;
        }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<PerspectiveDiscoveryGenerator>(source);

    await Assert.That(result.Diagnostics.Any(d => d.Id is "WHIZ030" or "WHIZ031")).IsFalse()
      .Because("an override on a record parameter is a stream id, so the event is not missing one");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task APerspectiveRunner_ExtractsTheOverrideParameterAsync() {
    const string source = """
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      namespace TestNamespace {
        public abstract record OrderEventBase : IEvent { [StreamId] public Guid OrderId { get; init; } }
        public record LineAdded([StreamId(Override = true)] Guid LineId) : OrderEventBase;
        public record LineModel { [StreamId] public Guid LineId { get; init; } }
        public class LinePerspective : IPerspectiveFor<LineModel, LineAdded> {
          public LineModel Apply(LineModel currentData, LineAdded @event) => currentData;
        }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);

    var runner = GeneratorTestHelper.GetGeneratedSource(result, "LinePerspectiveRunner.g.cs");
    await Assert.That(runner).IsNotNull();
    await Assert.That(runner).Contains("return @event.LineId.ToString();")
      .Because("the runner keys the event by the override, the same property the extractor uses");
    await Assert.That(runner).DoesNotContain("@event.OrderId");
  }
}
