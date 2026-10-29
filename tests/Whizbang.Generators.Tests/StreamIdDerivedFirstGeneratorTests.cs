// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// A derived message type with its own <c>[StreamId]</c> resolves and sets by that property, never by an inherited
/// one, whatever its namespace is called (#1893). The generated extractors dispatch with a chain of
/// <c>if (message is T)</c> checks; a base type's check emitted ahead of a derived type's matches every derived
/// instance and shadows the derived property. Each test compiles a contract and a probe that calls the generated
/// <c>StreamIdExtractors</c>, runs the probe, and reads which property every call used.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/StreamIdGenerator.cs</code-under-test>
[Category("SourceGenerators")]
public class StreamIdDerivedFirstGeneratorTests {
  private const string COMMAND = "ICommand";
  private const string EVENT = "IEvent";
  private const string ORDER_ID = "OrderId";
  private const string INVOICE_ID = "InvoiceId";

  private static readonly Guid _baseId = Guid.Parse("11111111-1111-1111-1111-111111111111");
  private static readonly Guid _ownId = Guid.Parse("22222222-2222-2222-2222-222222222222");
  private static readonly Guid _setId = Guid.Parse("33333333-3333-3333-3333-333333333333");

  /// <summary>
  /// Compiles <paramref name="contracts"/> and <paramref name="probe"/> with the stream id generator, loads the
  /// result and returns what <c>App.Probe.Run()</c> returned.
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
    var (_, output) = GeneratorTestHelper.RunGeneratorWithFullReferences(new StreamIdGenerator(), source, assemblyName);
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
  /// The probe body for a command or event <paramref name="type"/> with an inherited <c>StreamId</c> and its own
  /// <paramref name="ownProperty"/>: resolve, try-resolve, then set, and report every result.
  /// </summary>
  private static string _resolveAndSetProbe(string generatedNamespace, string type, string ownProperty, string messageInterface) => $$"""
    var message = new {{type}} { StreamId = new Guid("{{_baseId}}"), {{ownProperty}} = new Guid("{{_ownId}}") };
    var resolved = global::{{generatedNamespace}}.Generated.StreamIdExtractors.Resolve(({{messageInterface}})message);
    var tried = global::{{generatedNamespace}}.Generated.StreamIdExtractors.TryResolveAsGuid((object)message);
    var fresh = new {{type}}();
    var wrote = global::{{generatedNamespace}}.Generated.StreamIdExtractors.SetStreamId(fresh, new Guid("{{_setId}}"));
    return $"resolve={resolved};try={tried};set={wrote};own={fresh.{{ownProperty}}};base={fresh.StreamId}";
    """;

  private static string _expectedOwn() =>
    $"resolve={_ownId};try={_ownId};set=True;own={_setId};base={Guid.Empty}";

  [Test]
  [RequiresAssemblyFiles]
  public async Task ADerivedCommandInANamespaceAfterTheBase_ResolvesAndSetsByItsOwnStreamIdAsync() {
    const string contracts = """
      namespace App.Contracts.Common {
        public abstract class BaseCommand : ICommand { [StreamId] public virtual Guid StreamId { get; set; } }
      }
      namespace App.Contracts.Orders {
        public class ShipOrderCommand : App.Contracts.Common.BaseCommand { [StreamId] public Guid OrderId { get; set; } }
      }
      """;

    var observed = _runProbe("DerivedFirstCommandAfter", contracts,
      _resolveAndSetProbe("DerivedFirstCommandAfter", "App.Contracts.Orders.ShipOrderCommand", ORDER_ID, COMMAND));

    await Assert.That(observed).IsEqualTo(_expectedOwn())
      .Because("the base's namespace sorts first, and its check must not shadow the derived command's own [StreamId]");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ADerivedCommandOverrideInANamespaceAfterTheBase_ResolvesAndSetsByTheOverrideAsync() {
    const string contracts = """
      namespace App.Contracts.Common {
        public abstract class BaseCommand : ICommand { [StreamId] public virtual Guid StreamId { get; set; } }
      }
      namespace App.Contracts.Orders {
        public class ShipOrderCommand : App.Contracts.Common.BaseCommand { [StreamId(Override = true)] public Guid OrderId { get; set; } }
      }
      """;

    var observed = _runProbe("DerivedFirstCommandOverride", contracts,
      _resolveAndSetProbe("DerivedFirstCommandOverride", "App.Contracts.Orders.ShipOrderCommand", ORDER_ID, COMMAND));

    await Assert.That(observed).IsEqualTo(_expectedOwn())
      .Because("an override is the stream id, so the base's check must not reach the derived command first");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ADerivedCommandInANamespaceBeforeTheBase_ResolvesAndSetsByItsOwnStreamIdAsync() {
    // Discovery follows declaration order, and declarations follow file and namespace order, so a derived type in a
    // namespace that sorts first is discovered ahead of its base: the order that always resolved correctly.
    const string contracts = """
      namespace App.Contracts.Billing {
        public class ChargeCommand : App.Contracts.Common.BaseCommand { [StreamId] public Guid InvoiceId { get; set; } }
      }
      namespace App.Contracts.Common {
        public abstract class BaseCommand : ICommand { [StreamId] public virtual Guid StreamId { get; set; } }
      }
      """;

    var observed = _runProbe("DerivedFirstCommandBefore", contracts,
      _resolveAndSetProbe("DerivedFirstCommandBefore", "App.Contracts.Billing.ChargeCommand", INVOICE_ID, COMMAND));

    await Assert.That(observed).IsEqualTo(_expectedOwn())
      .Because("the same contract shape resolves the same way whichever namespace sorts first");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ADerivedCommandOverrideInANamespaceBeforeTheBase_ResolvesAndSetsByTheOverrideAsync() {
    const string contracts = """
      namespace App.Contracts.Billing {
        public class ChargeCommand : App.Contracts.Common.BaseCommand { [StreamId(Override = true)] public Guid InvoiceId { get; set; } }
      }
      namespace App.Contracts.Common {
        public abstract class BaseCommand : ICommand { [StreamId] public virtual Guid StreamId { get; set; } }
      }
      """;

    var observed = _runProbe("DerivedFirstCommandOverrideBefore", contracts,
      _resolveAndSetProbe("DerivedFirstCommandOverrideBefore", "App.Contracts.Billing.ChargeCommand", INVOICE_ID, COMMAND));

    await Assert.That(observed).IsEqualTo(_expectedOwn());
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ThreeLevelsOfCommands_EachResolvesAndSetsByItsNearestStreamIdAsync() {
    // Namespaces sort root, then middle, then leaf: the order that shadows both derived levels without the fix.
    const string contracts = """
      namespace App.A {
        public class RootCommand : ICommand { [StreamId] public virtual Guid StreamId { get; set; } }
      }
      namespace App.B {
        public class MiddleCommand : App.A.RootCommand { [StreamId] public Guid MiddleId { get; set; } }
      }
      namespace App.C {
        public class LeafCommand : App.B.MiddleCommand { [StreamId(Override = true)] public Guid LeafId { get; set; } }
      }
      """;
    var probe = $$"""
      var leaf = new App.C.LeafCommand { StreamId = new Guid("{{_baseId}}"), MiddleId = new Guid("{{_baseId}}"), LeafId = new Guid("{{_ownId}}") };
      var middle = new App.B.MiddleCommand { StreamId = new Guid("{{_baseId}}"), MiddleId = new Guid("{{_ownId}}") };
      var root = new App.A.RootCommand { StreamId = new Guid("{{_ownId}}") };
      var x = global::DerivedFirstThreeCommands.Generated.StreamIdExtractors.TryResolveAsGuid((ICommand)leaf);
      var y = global::DerivedFirstThreeCommands.Generated.StreamIdExtractors.Resolve((ICommand)middle);
      var z = global::DerivedFirstThreeCommands.Generated.StreamIdExtractors.Resolve((ICommand)root);
      var freshLeaf = new App.C.LeafCommand();
      global::DerivedFirstThreeCommands.Generated.StreamIdExtractors.SetStreamId(freshLeaf, new Guid("{{_setId}}"));
      var freshMiddle = new App.B.MiddleCommand();
      global::DerivedFirstThreeCommands.Generated.StreamIdExtractors.SetStreamId(freshMiddle, new Guid("{{_setId}}"));
      return $"leaf={x};middle={y};root={z};setLeaf={freshLeaf.LeafId}/{freshLeaf.MiddleId}/{freshLeaf.StreamId};setMiddle={freshMiddle.MiddleId}/{freshMiddle.StreamId}";
      """;

    var observed = _runProbe("DerivedFirstThreeCommands", contracts, probe);

    await Assert.That(observed).IsEqualTo(
      $"leaf={_ownId};middle={_ownId};root={_ownId};setLeaf={_setId}/{Guid.Empty}/{Guid.Empty};setMiddle={_setId}/{Guid.Empty}")
      .Because("every level resolves and sets by its own nearest [StreamId], two levels down as much as one");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ADerivedCommandWithoutItsOwnStreamId_StillResolvesAndSetsByTheBaseAsync() {
    const string contracts = """
      namespace App.Contracts.Common {
        public abstract class BaseCommand : ICommand { [StreamId] public virtual Guid StreamId { get; set; } }
      }
      namespace App.Contracts.Orders {
        public class ShipOrderCommand : App.Contracts.Common.BaseCommand { [StreamId] public Guid OrderId { get; set; } }
        public class CancelOrderCommand : App.Contracts.Common.BaseCommand { public Guid Reason { get; set; } }
      }
      """;
    var probe = $$"""
      var message = new App.Contracts.Orders.CancelOrderCommand { StreamId = new Guid("{{_ownId}}"), Reason = new Guid("{{_baseId}}") };
      var resolved = global::DerivedFirstInherited.Generated.StreamIdExtractors.Resolve((ICommand)message);
      var fresh = new App.Contracts.Orders.CancelOrderCommand();
      global::DerivedFirstInherited.Generated.StreamIdExtractors.SetStreamId(fresh, new Guid("{{_setId}}"));
      return $"resolve={resolved};set={fresh.StreamId};reason={fresh.Reason}";
      """;

    var observed = _runProbe("DerivedFirstInherited", contracts, probe);

    await Assert.That(observed).IsEqualTo($"resolve={_ownId};set={_setId};reason={Guid.Empty}")
      .Because("a derived type with no [StreamId] of its own still uses the one it inherits");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ADerivedEventInANamespaceAfterTheBase_ResolvesAndSetsByItsOwnStreamIdAsync() {
    const string contracts = """
      namespace App.Contracts.Common {
        public abstract class BaseEvent : IEvent { [StreamId] public virtual Guid StreamId { get; set; } }
      }
      namespace App.Contracts.Orders {
        public class OrderShipped : App.Contracts.Common.BaseEvent { [StreamId] public Guid OrderId { get; set; } }
      }
      """;

    var observed = _runProbe("DerivedFirstEventAfter", contracts,
      _resolveAndSetProbe("DerivedFirstEventAfter", "App.Contracts.Orders.OrderShipped", ORDER_ID, EVENT));

    await Assert.That(observed).IsEqualTo(_expectedOwn())
      .Because("events dispatch the same way as commands, most-derived first");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ADerivedEventOverrideInANamespaceBeforeTheBase_ResolvesAndSetsByTheOverrideAsync() {
    const string contracts = """
      namespace App.Contracts.Billing {
        public class InvoiceRaised : App.Contracts.Common.BaseEvent { [StreamId(Override = true)] public Guid InvoiceId { get; set; } }
      }
      namespace App.Contracts.Common {
        public abstract class BaseEvent : IEvent { [StreamId] public virtual Guid StreamId { get; set; } }
      }
      """;

    var observed = _runProbe("DerivedFirstEventBefore", contracts,
      _resolveAndSetProbe("DerivedFirstEventBefore", "App.Contracts.Billing.InvoiceRaised", INVOICE_ID, EVENT));

    await Assert.That(observed).IsEqualTo(_expectedOwn());
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ThreeLevelsOfEvents_EachResolvesByItsNearestStreamIdAsync() {
    const string contracts = """
      namespace App.A {
        public class RootEvent : IEvent { [StreamId] public virtual Guid StreamId { get; set; } }
      }
      namespace App.B {
        public class MiddleEvent : App.A.RootEvent { [StreamId] public Guid MiddleId { get; set; } }
      }
      namespace App.C {
        public class LeafEvent : App.B.MiddleEvent { [StreamId] public Guid LeafId { get; set; } }
      }
      """;
    var probe = $$"""
      var leaf = new App.C.LeafEvent { StreamId = new Guid("{{_baseId}}"), MiddleId = new Guid("{{_baseId}}"), LeafId = new Guid("{{_ownId}}") };
      var middle = new App.B.MiddleEvent { StreamId = new Guid("{{_baseId}}"), MiddleId = new Guid("{{_ownId}}") };
      var x = global::DerivedFirstThreeEvents.Generated.StreamIdExtractors.Resolve((IEvent)leaf);
      var y = global::DerivedFirstThreeEvents.Generated.StreamIdExtractors.TryResolveAsGuid((IEvent)middle);
      var freshLeaf = new App.C.LeafEvent();
      global::DerivedFirstThreeEvents.Generated.StreamIdExtractors.SetStreamId(freshLeaf, new Guid("{{_setId}}"));
      return $"leaf={x};middle={y};setLeaf={freshLeaf.LeafId}/{freshLeaf.MiddleId}/{freshLeaf.StreamId}";
      """;

    var observed = _runProbe("DerivedFirstThreeEvents", contracts, probe);

    await Assert.That(observed).IsEqualTo($"leaf={_ownId};middle={_ownId};setLeaf={_setId}/{Guid.Empty}/{Guid.Empty}");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ADerivedTypesOwnGenerationPolicy_IsNotShadowedByTheBaseAsync() {
    const string contracts = """
      namespace App.Contracts.Common {
        public abstract class BaseCommand : ICommand { [StreamId][GenerateStreamId] public virtual Guid StreamId { get; set; } }
      }
      namespace App.Contracts.Orders {
        public class ShipOrderCommand : App.Contracts.Common.BaseCommand {
          [StreamId][GenerateStreamId(OnlyIfEmpty = true)] public Guid OrderId { get; set; }
        }
      }
      """;
    const string probe = """
      var policy = global::DerivedFirstPolicy.Generated.StreamIdExtractors.GetGenerationPolicy(new App.Contracts.Orders.ShipOrderCommand());
      return $"generate={policy.ShouldGenerate};onlyIfEmpty={policy.OnlyIfEmpty}";
      """;

    var observed = _runProbe("DerivedFirstPolicy", contracts, probe);

    await Assert.That(observed).IsEqualTo("generate=True;onlyIfEmpty=True")
      .Because("the generation policy lookup dispatches most-derived first, like every other region");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task TypesAtTheSameDepth_AreDispatchedInNameOrderWhateverTheDeclarationOrderAsync() {
    const string source = """
      using System;
      using Whizbang.Core;
      namespace App.Z { public class Zebra : ICommand { [StreamId] public Guid Id { get; set; } } }
      namespace App.A { public class Antelope : ICommand { [StreamId] public Guid Id { get; set; } } }
      """;

    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "StreamIdExtractors.g.cs") ?? "";

    await Assert.That(generated).Contains("command is global::App.A.Antelope c0")
      .Because("equal depths fall back to the type name, so the generated file does not depend on discovery order");
    await Assert.That(generated).Contains("command is global::App.Z.Zebra c1");
  }
}
