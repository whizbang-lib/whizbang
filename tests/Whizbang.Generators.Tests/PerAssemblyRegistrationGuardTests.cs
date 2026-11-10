// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The guard behind the one rule for per-assembly registrations (#2201): no generated code registers a host-wide
/// service with an implementation it generated, except through a contribution.
/// </summary>
/// <remarks>
/// <para>
/// Every assembly compiled with these generators emits the same registrations, so a generated
/// <c>AddSingleton&lt;IService, GeneratedImplementation&gt;()</c> of a service the host resolves once lets the last
/// assembly registered displace every other's, with no error and no log (#2146, #2191, #2197, #2198, #2200). The
/// contribution calls (<c>Add...Contribution&lt;T&gt;()</c>) are the correct path; this test makes the other path
/// fail the build of a new generator.
/// </para>
/// <para>
/// It runs every generator in Whizbang.Generators over one source that declares every kind of per-assembly item, and
/// reads every service registration in the generated code. A registration is a bypass when its service is a
/// non-generic Whizbang.Core interface (a service resolved once, not one per message or model type) and its
/// implementation, or the factory that builds it, is a type the generators emitted. The few that are correct by
/// another means are listed with the reason.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Generators</code-under-test>
public partial class PerAssemblyRegistrationGuardTests {
  private const string SOURCE = """
    using System;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Whizbang.Core;
    using Whizbang.Core.Attributes;
    using Whizbang.Core.Messaging;
    using Whizbang.Core.Perspectives;

    namespace GuardApp;

    [PinnedId("7b0e1d5c-2f43-4a7e-9d21-5b6c8e9f0a12")]
    [Topic("orders")]
    public record OrderPlaced : IEvent {
      [StreamId]
      public Guid OrderId { get; init; }
    }

    public record PlaceOrder : ICommand {
      [StreamId]
      public Guid OrderId { get; init; }
    }

    public record OrderModel {
      [StreamId]
      public Guid Id { get; init; }
    }

    public class OrderPerspective : IPerspectiveFor<OrderModel, OrderPlaced> {
      public OrderModel Apply(OrderModel current, OrderPlaced @event) => current;
    }

    public class PlaceOrderReceptor : IReceptor<PlaceOrder, OrderPlaced> {
      public ValueTask<OrderPlaced> HandleAsync(PlaceOrder message, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new OrderPlaced { OrderId = message.OrderId });
    }

    [FireAt(LifecycleStage.PostPerspectiveInline)]
    public class OrderAuditReceptor : IReceptor<OrderPlaced> {
      public ValueTask HandleAsync(OrderPlaced message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    public class OrderRawReceptor : IRawReceptor {
      public string TargetMessageTypeName => "GuardApp.OrderPlaced, GuardApp";
      public Task HandleAsync(JsonElement payload, CancellationToken ct) => Task.CompletedTask;
    }
    """;

  /// <summary>Registrations of a host-wide service with a generated implementation that are correct by another means.</summary>
  private static readonly Dictionary<string, string> _correctByAnotherMeans = new(StringComparer.Ordinal) {
    ["IDispatcher"] = "every assembly registers its dispatcher, and the one resolved falls back to every other "
      + "assembly's receptor lookups, contributed as IReceptorLookup (#491)",
    ["IMessageTypeCatalog"] = "each assembly's registration runs against a probe collection, and the container sees only "
      + "the CompositeMessageTypeCatalog union the slot registers",
    ["ITopicRegistry"] = "registered only by an explicit AddTopicRegistry() call the host makes for its own assembly; "
      + "never generated into an automatic registration",
  };

  // A declaration at the start of a line, so the words "class" or "record" in a comment are not taken for one.
  [GeneratedRegex(@"^\s*(?:(?:public|internal|private|protected|file|sealed|static|partial|abstract|readonly)\s+)*(?:class|record|struct)\s+(\w+)", RegexOptions.Multiline)]
  private static partial Regex _declaredType();

  [GeneratedRegex(@"\b(?:Add|TryAdd)(?:Singleton|Scoped|Transient)\s*<\s*(?:global::)?([\w.]+?)\s*(?:,\s*(?:global::)?([\w.]+?)\s*)?>\s*\(([^;]*)")]
  private static partial Regex _registration();

  /// <summary>Every registration in <paramref name="code"/> that registers a host-wide service with a generated implementation.</summary>
  private static List<string> _bypasses(string code) {
    var coreTypes = typeof(Whizbang.Core.IDispatcher).Assembly.GetExportedTypes();
    // A name the generated code declares that Whizbang.Core also exports is Core's type mentioned in the text (a
    // comment, a delegate), not a per-assembly implementation.
    var coreNames = coreTypes.Select(t => t.Name.Split('`')[0]).ToHashSet(StringComparer.Ordinal);
    var generatedTypes = _declaredType().Matches(code).Select(m => m.Groups[1].Value)
      .Where(name => !coreNames.Contains(name))
      .ToHashSet(StringComparer.Ordinal);
    var hostWide = coreTypes
      .Where(t => t.IsInterface && !t.IsGenericType)
      .Select(t => t.Name)
      .ToHashSet(StringComparer.Ordinal);
    var bypasses = new List<string>();
    foreach (Match registration in _registration().Matches(code)) {
      var service = registration.Groups[1].Value.Split('.')[^1];
      if (!hostWide.Contains(service) || _correctByAnotherMeans.ContainsKey(service)) {
        continue;
      }
      var implementation = registration.Groups[2].Value.Split('.')[^1];
      var isGenerated = implementation.Length > 0
        ? generatedTypes.Contains(implementation)
        : generatedTypes.Any(name => Regex.IsMatch(registration.Groups[3].Value, $@"\b{Regex.Escape(name)}\b", RegexOptions.None, TimeSpan.FromSeconds(1)));
      if (isGenerated) {
        bypasses.Add(registration.Value.Trim());
      }
    }
    return bypasses;
  }

  /// <summary>The positive control: the detector finds both forms of a bypass, and passes what is not one.</summary>
  [Test]
  public async Task TheGuard_FindsABypass_ByTypeArgumentOrByFactoryAsync() {
    const string code = """
      public sealed class GeneratedThingRegistry : IPinnedIdRegistry { }
      public sealed class GeneratedThingInvoker : IPerspectiveInvoker { }
      services.AddSingleton<IPinnedIdRegistry, GeneratedThingRegistry>();
      services.TryAddScoped<global::Whizbang.Core.Perspectives.IPerspectiveInvoker>(sp => new GeneratedThingInvoker());
      services.AddSingleton<IPinnedIdRegistry, SomeHostRegistry>();
      services.AddSingleton<IReceptor<Foo>, GeneratedThingRegistry>();
      services.AddSingleton<IDispatcher>(sp => new GeneratedThingRegistry());
      """;

    var bypasses = _bypasses(code);

    await Assert.That(bypasses.Count).IsEqualTo(2)
      .Because("a generated implementation named as the type argument or built by the factory is a bypass; a host's "
             + "own implementation, a per-message-type service and a listed exception are not; found: "
             + string.Join(" | ", bypasses));
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task EveryGeneratedRegistrationOfAHostWideService_GoesThroughAContributionAsync() {
    var generatorTypes = typeof(ReceptorDiscoveryGenerator).Assembly.GetTypes()
      .Where(t => !t.IsAbstract && typeof(IIncrementalGenerator).IsAssignableFrom(t)
        && t.GetCustomAttributes(typeof(GeneratorAttribute), inherit: false).Length > 0)
      .ToList();
    var generated = new List<string>();
    foreach (var type in generatorTypes) {
      var generator = (IIncrementalGenerator)Activator.CreateInstance(type)!;
      var (result, _) = GeneratorTestHelper.RunGeneratorWithFullReferences(generator, SOURCE, "GuardApp");
      generated.AddRange(result.GeneratedTrees.Select(tree => tree.ToString()));
    }
    var code = string.Join("\n", generated);
    var bypasses = _bypasses(code);

    await Assert.That(generatorTypes.Count).IsGreaterThan(10)
      .Because("precondition: the guard runs every generator in the assembly, found by type");
    string[] contributions = [
      "AddPerspectiveRunnerRegistryContribution<PerspectiveRunnerRegistry>",
      "AddReceptorRegistryContribution<GeneratedReceptorRegistry>",
      "AddPinnedIdRegistryContribution<GeneratedPinnedIdRegistry>",
      "AddPerspectiveInvokerContribution<GeneratedPerspectiveInvoker>",
    ];
    foreach (var contribution in contributions) {
      await Assert.That(code).Contains(contribution)
        .Because("precondition: the source declares every kind of per-assembly item, so each contribution is generated");
    }
    await Assert.That(bypasses).IsEmpty()
      .Because("a generated registration of a host-wide service lets the last assembly hide every other's; contribute "
             + "through an Add...Contribution<T>() call instead (#2201). Found: " + string.Join(" | ", bypasses));
  }
}
