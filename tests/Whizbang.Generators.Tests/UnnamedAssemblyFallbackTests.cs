// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// A compilation without an assembly name still gets generated code, under each generator's
/// documented fallback namespace or name. <see cref="Compilation.AssemblyName"/> is nullable, and
/// every generator that derives a namespace from it says what to use instead; these tests pin
/// that answer per generator, so a fallback that changed (or became an empty string, which would
/// emit <c>namespace .Generated</c> and break the consumer's build) fails here.
/// </summary>
/// <tests>src/Whizbang.Generators</tests>
/// <tests>src/Whizbang.Data.EFCore.Postgres.Generators</tests>
[Category("SourceGenerators")]
public partial class UnnamedAssemblyFallbackTests {
  private const string PERSPECTIVE_SOURCE = """
    using System;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;

    namespace TestNamespace {
      public record OrderCreatedEvent : IEvent {
        [StreamId] public Guid OrderId { get; init; }
      }

      public record OrderModel {
        [StreamId] public Guid OrderId { get; set; }
      }

      public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
        public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) => currentData;
      }
    }
    """;

  private const string RECEPTOR_SOURCE = """
    using System.Threading;
    using System.Threading.Tasks;
    using Whizbang.Core;

    namespace TestNamespace {
      public record PlaceOrder(string Id) : ICommand;
      public record OrderPlaced(string Id) : IEvent;

      public class PlaceOrderReceptor : IReceptor<PlaceOrder, OrderPlaced> {
        public ValueTask<OrderPlaced> HandleAsync(PlaceOrder message, CancellationToken cancellationToken = default) =>
          ValueTask.FromResult(new OrderPlaced(message.Id));
      }
    }
    """;

  [GeneratedRegex(@"^\s*namespace\s+([\w.]+)", RegexOptions.Multiline)]
  private static partial Regex _namespaceDeclaration();

  private static List<string> _namespaces(GeneratorDriverRunResult result) =>
    [.. result.Results
      .SelectMany(r => r.GeneratedSources)
      .SelectMany(s => _namespaceDeclaration().Matches(s.SourceText.ToString()).Select(m => m.Groups[1].Value))
      .Distinct(StringComparer.Ordinal)];

  private static string _allSource(GeneratorDriverRunResult result) =>
    string.Concat(result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.SourceText.ToString()));

  private static async Task _assertFallbackNamespaceAsync(IIncrementalGenerator generator, string source, string expected) {
    var result = GeneratorTestHelper.RunGeneratorOnUnnamedAssembly(generator, source);
    var namespaces = _namespaces(result);

    await Assert.That(namespaces).Contains(expected)
      .Because($"an unnamed assembly's generated code goes under the fallback namespace {expected}");
    await Assert.That(namespaces.Where(n => n.StartsWith('.'))).IsEmpty()
      .Because("a namespace derived from a missing name would not compile");
  }

  private static async Task _assertFallbackNameAsync(IIncrementalGenerator generator, string source, string expected) {
    var result = GeneratorTestHelper.RunGeneratorOnUnnamedAssembly(generator, source);

    await Assert.That(_allSource(result)).Contains(expected)
      .Because($"an unnamed assembly's generated names are built from the fallback, giving {expected}");
  }

  [Test]
  public Task EventNamespaceRegistry_UsesUnknownAssemblyAsync() =>
    _assertFallbackNamespaceAsync(new EventNamespaceRegistryGenerator(), PERSPECTIVE_SOURCE, "UnknownAssembly.Generated");

  [Test]
  public Task MessageTypeCatalog_UsesUnknownAssemblyAsync() =>
    _assertFallbackNamespaceAsync(new MessageTypeCatalogGenerator(), RECEPTOR_SOURCE, "UnknownAssembly.Generated");

  [Test]
  public Task PerspectiveDiscovery_UsesWhizbangCoreAsync() =>
    _assertFallbackNamespaceAsync(new PerspectiveDiscoveryGenerator(), PERSPECTIVE_SOURCE, "Whizbang.Core.Generated");

  [Test]
  public Task PerspectiveRunner_UsesWhizbangCoreAsync() =>
    _assertFallbackNamespaceAsync(new PerspectiveRunnerGenerator(), PERSPECTIVE_SOURCE, "Whizbang.Core.Generated");

  [Test]
  public Task PerspectiveInvoker_UsesWhizbangCoreAsync() =>
    _assertFallbackNamespaceAsync(new PerspectiveInvokerGenerator(), PERSPECTIVE_SOURCE, "Whizbang.Core.Generated");

  [Test]
  public Task PerspectiveRunnerRegistry_UsesWhizbangCoreAsync() =>
    _assertFallbackNamespaceAsync(new PerspectiveRunnerRegistryGenerator(), PERSPECTIVE_SOURCE, "Whizbang.Core.Generated");

  [Test]
  public Task StreamId_UsesWhizbangCoreAsync() =>
    _assertFallbackNamespaceAsync(new StreamIdGenerator(), PERSPECTIVE_SOURCE, "Whizbang.Core.Generated");

  [Test]
  public Task ReceptorDiscovery_UsesWhizbangCoreAsync() =>
    _assertFallbackNamespaceAsync(new ReceptorDiscoveryGenerator(), RECEPTOR_SOURCE, "Whizbang.Core.Generated");

  [Test]
  public Task ServiceRegistration_UsesWhizbangCoreAsync() =>
    _assertFallbackNamespaceAsync(new ServiceRegistrationGenerator(), PERSPECTIVE_SOURCE, "Whizbang.Core.Generated");

  [Test]
  public Task EFCorePerspectiveAssociation_UsesWhizbangCoreAsync() =>
    _assertFallbackNamespaceAsync(new EFCorePerspectiveAssociationGenerator(), PERSPECTIVE_SOURCE, "Whizbang.Core.Generated");

  [Test]
  public Task PinnedIdRegistry_UsesUnknownAssemblyAsync() =>
    _assertFallbackNamespaceAsync(new PinnedIdRegistryGenerator(), """
      using Whizbang.Core;
      using Whizbang.Core.Attributes;

      namespace MyApp.Events;

      [PinnedId("11111111-2222-3333-4444-555555555555")]
      public record OrderPlacedEvent : IEvent;
      """, "UnknownAssembly.Generated");

  [Test]
  public Task TopicFilter_UsesGeneratedAsync() =>
    _assertFallbackNamespaceAsync(new TopicFilterGenerator(), """
      using Whizbang.Core;

      namespace TestNamespace;

      [TopicFilter("orders.create")]
      public record CreateOrderCommand : ICommand;
      """, "Generated.Generated");

  [Test]
  public Task TopicRegistry_UsesUnknownAssemblyAsync() =>
    _assertFallbackNamespaceAsync(new TopicRegistryGenerator(), """
      using Whizbang.Core;
      using Whizbang.Core.Attributes;

      namespace MyApp.Events;

      [Topic("products")]
      public record ProductCreatedEvent : IEvent;
      """, "UnknownAssembly.Generated");

  [Test]
  public Task SignalTypeRegistry_UsesUnknownAssemblyAsync() =>
    _assertFallbackNamespaceAsync(new SignalTypeRegistryGenerator(), """
      using Whizbang.Core.Signals;

      namespace App.Signals {
        public readonly record struct CacheInvalidated(string Region) : ISignal {
          public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
          public static SignalTargeting Targeting => SignalTargeting.Broadcast;
        }
      }
      """, "UnknownAssembly.Generated");

  [Test]
  public Task RawReceptorDiscovery_UsesUnknownAssemblyAsync() =>
    _assertFallbackNamespaceAsync(new RawReceptorDiscoveryGenerator(), """
      using System.Text.Json;
      using System.Threading;
      using System.Threading.Tasks;
      using Whizbang.Core.Messaging;

      namespace MyApp;

      public class FooRawReceptor : IRawReceptor {
        public string TargetMessageTypeName => "MyApp.Events.Foo, MyApp.Contracts";
        public Task HandleAsync(JsonElement payload, CancellationToken ct) => Task.CompletedTask;
      }
      """, "UnknownAssembly.Generated");

  [Test]
  public Task SyncEventTypeRegistry_UsesWhizbangCoreAsync() =>
    _assertFallbackNamespaceAsync(new SyncEventTypeRegistryGenerator(), """
      using System;
      using Whizbang.Core.Perspectives.Sync;

      namespace TestApp {
        public class OrderCreatedEvent { public string OrderId { get; set; } = ""; }
        public class OrderPerspective { }

        [AwaitPerspectiveSync(typeof(OrderPerspective), EventTypes = new[] { typeof(OrderCreatedEvent) })]
        public class OrderHandler { }
      }
      """, "Whizbang.Core.Generated");

  [Test]
  public Task WhizbangId_UsesWhizbangCoreAsync() =>
    _assertFallbackNamespaceAsync(new WhizbangIdGenerator(), """
      using Whizbang.Core;

      namespace MyApp.Domain;

      [WhizbangId]
      public readonly partial struct OrderId;
      """, "Whizbang.Core.Generated");

  [Test]
  public Task MessageTagDiscovery_UsesUnknownAsync() =>
    _assertFallbackNameAsync(new MessageTagDiscoveryGenerator(), """
      using System;
      using Whizbang.Core.Attributes;

      namespace TestApp;

      [SignalTag(Tag = "order-created", Properties = ["OrderId"])]
      public record OrderCreatedEvent(Guid OrderId);
      """, "class GeneratedMessageTagRegistry_Unknown");

  [Test]
  public Task AutoPopulateDiscovery_UsesUnknownAsync() =>
    _assertFallbackNameAsync(new AutoPopulateDiscoveryGenerator(), """
      using Whizbang.Core.Attributes;

      namespace TestApp;

      public class OrderCreatedEvent {
        [PopulateFromHttpHeader("X-Correlation-ID")] public string? CorrelationId { get; set; }
      }
      """, "class GeneratedAutoPopulateRegistry_Unknown");

  [Test]
  public Task ServiceRequirements_UsesWhizbangCoreAsync() =>
    _assertFallbackNameAsync(new ServiceRequirementsGenerator(), """
      using Microsoft.Extensions.DependencyInjection;
      namespace TestApp;
      public interface IClock { }
      public sealed class Worker {
        public Worker(IClock clock) { }
      }
      public static class Registration {
        public static IServiceCollection AddThing(this IServiceCollection services) {
          services.AddSingleton<Worker>();
          return services;
        }
      }
      """, "namespace Whizbang.Core.DependencyInjection;");

  [Test]
  public Task PerspectivePersistenceJsonContext_UsesGeneratedAsync() =>
    _assertFallbackNamespaceAsync(new PerspectivePersistenceJsonContextGenerator(), PERSPECTIVE_SOURCE, "Generated.Generated");

  [Test]
  public Task MessageJsonContext_UsesWhizbangCoreAsync() =>
    _assertFallbackNamespaceAsync(new MessageJsonContextGenerator(), RECEPTOR_SOURCE, "Whizbang.Core.Generated");

  /// <summary>
  /// The message JSON context keys each type by its assembly-qualified name, so an unnamed
  /// assembly's types are keyed under the fallback name rather than under an empty one.
  /// </summary>
  [Test]
  public async Task MessageJsonContext_KeysTypesUnderUnknownAssemblyAsync() {
    var result = GeneratorTestHelper.RunGeneratorOnUnnamedAssembly(new MessageJsonContextGenerator(), RECEPTOR_SOURCE);
    var source = _allSource(result);

    await Assert.That(source).Contains("TestNamespace.PlaceOrder, Unknown");
    await Assert.That(source).DoesNotContain("TestNamespace.PlaceOrder, \"");
  }

  /// <summary>The generator's own progress note names the fallback when there is no assembly name.</summary>
  [Test]
  public async Task MessageJsonContext_RunningNote_NamesUnknownAssemblyAsync() {
    var result = GeneratorTestHelper.RunGeneratorOnUnnamedAssembly(new MessageJsonContextGenerator(), RECEPTOR_SOURCE);
    var note = result.Diagnostics.Single(d => d.Id == "WHIZ099");

    await Assert.That(note.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).StartsWith("MessageJsonContextGenerator invoked for assembly 'Unknown'");
  }

  /// <summary>
  /// The schema initializer registers a context's perspective associations under the assembly's
  /// name, and an unnamed assembly registers them under the fallback name.
  /// </summary>
  [Test]
  public async Task EFCoreServiceRegistration_RegistersAssociationsUnderUnknownAsync() {
    var result = GeneratorTestHelper.RunGeneratorOnUnnamedAssembly(new EFCoreServiceRegistrationGenerator(), """
      using System;
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      public record OrderCreatedEvent : IEvent {
        [StreamId] public Guid OrderId { get; init; }
      }

      public record OrderModel {
        [StreamId] public Guid OrderId { get; set; }
      }

      public class OrderPerspective : IPerspectiveFor<OrderModel, OrderCreatedEvent> {
        public OrderModel Apply(OrderModel currentData, OrderCreatedEvent @event) => currentData;
      }

      [WhizbangDbContext]
      public class TestDbContext : DbContext {
        public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """);
    var source = _allSource(result);

    await Assert.That(source).Contains("RegisterPerspectiveAssociationsAsync(")
      .Because("the control: the context has a perspective, so its associations are registered");
    await Assert.That(source).Contains("\"Unknown\", logger, cancellationToken");
    await Assert.That(source).Contains("global::Unknown.Generated.EFCorePerspectiveAssociationExtensions.AssociationsHash");
  }

  /// <summary>
  /// With nothing to dispatch, an assembly is told it has no receptors (WHIZ002) unless its name
  /// says it is a test project. An unnamed assembly has no name to say so, so it is warned.
  /// </summary>
  [Test]
  public async Task ReceptorDiscovery_NoReceptorsInUnnamedAssembly_WarnsAsync() {
    var result = GeneratorTestHelper.RunGeneratorOnUnnamedAssembly(new ReceptorDiscoveryGenerator(), """
      namespace Empty;
      public class Nothing { }
      """);

    await Assert.That(result.Diagnostics.Select(d => d.Id)).Contains("WHIZ002");
  }
}
