// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Serialization;

/// <summary>
/// Branch backfill for <see cref="JsonContextRegistry"/>: the interface-typed envelope writes a null
/// hop list as an empty array, and the lazy polymorphic builder treats options with no resolver
/// chain as able to resolve no derived type.
/// </summary>
/// <remarks>
/// Shares the "JsonContextRegistryMutation" not-in-parallel group with the other tests that call
/// RegisterDerivedType, which mutates a process-wide registry.
/// </remarks>
[NotInParallel("JsonContextRegistryMutation")]
[Category("Core")]
[Category("Serialization")]
public class JsonContextRegistryBranchCoverageTests {

  /// <summary>A base registered only by this class.</summary>
  public abstract record ResolverlessBase;

  public sealed record ResolverlessChild : ResolverlessBase {
    public string Name { get; init; } = string.Empty;
  }

  [Test]
  public async Task InterfaceTypedEnvelope_NullHops_SerializesAnEmptyHopArrayAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var typeInfo = JsonContextRegistry.GetPolymorphicEnvelopeTypeInfo<IEvent>(options)!;
    var envelope = new MessageEnvelope<IEvent> {
      MessageId = MessageId.New(),
      Payload = new OriginWireProbeEvent(Guid.Parse("0b6e3c1d-2f4a-4e5b-8c7d-6a5b4c3d2e1f"), "no hops"),
      Hops = null!,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Both, Source = MessageSource.Outbox },
    };

    var node = JsonNode.Parse(JsonSerializer.Serialize(envelope, typeInfo))!.AsObject();

    await Assert.That(node["Hops"]).IsTypeOf<JsonArray>()
      .Because("a null hop list is written as an empty array, so a reader never meets a null where a list belongs");
    await Assert.That(node["Hops"]!.AsArray().Count).IsEqualTo(0);
  }

  [Test]
  public async Task GetLazyPolymorphicTypeInfo_OptionsWithNoResolver_RegistersNoDerivedTypesAsync() {
    JsonContextRegistry.RegisterDerivedType<ResolverlessBase, ResolverlessChild>();
    var options = new JsonSerializerOptions();

    var info = JsonContextRegistry.GetLazyPolymorphicTypeInfo<ResolverlessBase>(options);

    await Assert.That(options.TypeInfoResolver).IsNull()
      .Because("precondition: these options carry no resolver chain");
    await Assert.That(info).IsNotNull()
      .Because("precondition: the base has a registered derived type, so polymorphic metadata is built");
    await Assert.That(info!.PolymorphismOptions!.DerivedTypes.Count).IsEqualTo(0)
      .Because("with no resolver chain no derived type is resolvable, and an unresolvable one must be skipped "
             + "rather than fail the whole base when it is finalized");
  }
}
