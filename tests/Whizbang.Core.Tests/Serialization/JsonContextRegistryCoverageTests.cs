using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Serialization;

/// <summary>
/// Targeted coverage for the remaining uncovered branches of <see cref="JsonContextRegistry"/>:
/// duplicate-discriminator determinism (both the eager and the lazy builder), a resolver that THROWS
/// instead of returning null while its resolvability is being probed, a composite whose own nested
/// members reach back into the lazy accessors while it is itself being trial-configured, and the
/// polymorphic list metadata's <c>ObjectCreator</c> actually running (not merely existing).
/// </summary>
/// <remarks>
/// Shares the "JsonContextRegistryMutation" not-in-parallel group with
/// <see cref="JsonContextRegistryLazyPolymorphicTests"/> and <see cref="PolymorphicQuarantineTests"/>:
/// all three mutate the process-wide derived-type/resolver registries and spin dedicated
/// trial-configure threads. A distinct key would let them register and trial-configure concurrently
/// and interleave with each other.
/// </remarks>
[NotInParallel("JsonContextRegistryMutation")]
[Category("Core")]
[Category("Serialization")]
public class JsonContextRegistryCoverageTests {

  // --- Duplicate discriminator: eager and lazy builders must agree, deterministically -----------

  public interface IDuplicateDiscriminatorBase;

  public sealed record DuplicateDiscriminatorFirst : IDuplicateDiscriminatorBase {
    public int Marker { get; init; }
  }

  public sealed record DuplicateDiscriminatorSecond : IDuplicateDiscriminatorBase {
    public int Marker { get; init; }
  }

  private static void _registerDuplicateDiscriminatorTypes() {
    JsonContextRegistry.RegisterContext(DuplicateDiscriminatorJsonContext.Default);
    JsonContextRegistry.RegisterDerivedType<IDuplicateDiscriminatorBase, DuplicateDiscriminatorFirst>("Dup");
    JsonContextRegistry.RegisterDerivedType<IDuplicateDiscriminatorBase, DuplicateDiscriminatorSecond>("Dup");
  }

  /// <summary>
  /// Two derived types registering the SAME wire discriminator must resolve to exactly one winner,
  /// always the same one. If the eager builder ever let both through (or picked inconsistently), a
  /// "$type":"Dup" payload would deserialize to a DIFFERENT concrete type depending on assembly load
  /// order — corrupting whichever message actually hit the wire second.
  /// </summary>
  [Test]
  public async Task GetPolymorphicTypeInfo_DuplicateDiscriminator_KeepsOnlyTheFirstEncounteredRegistrationAsync() {
    _registerDuplicateDiscriminatorTypes();
    var expectedWinner = JsonContextRegistry.GetRegisteredDerivedTypes<IDuplicateDiscriminatorBase>().First();

    var options = JsonContextRegistry.CreateCombinedOptions();
    var typeInfo = JsonContextRegistry.GetPolymorphicTypeInfo<IDuplicateDiscriminatorBase>(options);

    var result = JsonSerializer.Deserialize("{\"$type\":\"Dup\",\"Marker\":1}", typeInfo!);

    await Assert.That(result!.GetType()).IsEqualTo(expectedWinner)
      .Because("the eager builder must keep only the first-encountered registration for a duplicated discriminator, deterministically");
  }

  /// <summary>
  /// Same determinism guarantee, but for the LAZY builder that backs a composite's nested inner-
  /// message list. If the two builders ever disagreed, the same duplicated discriminator would
  /// resolve to a different concrete type depending on whether it arrived as a top-level envelope
  /// payload or nested inside a composite's inner-event list.
  /// </summary>
  [Test]
  public async Task GetLazyPolymorphicTypeInfo_DuplicateDiscriminator_KeepsOnlyTheFirstEncounteredRegistrationAsync() {
    _registerDuplicateDiscriminatorTypes();
    var expectedWinner = JsonContextRegistry.GetRegisteredDerivedTypes<IDuplicateDiscriminatorBase>().First();

    var options = JsonContextRegistry.CreateCombinedOptions();
    var typeInfo = JsonContextRegistry.GetLazyPolymorphicTypeInfo<IDuplicateDiscriminatorBase>(options);

    var result = JsonSerializer.Deserialize("{\"$type\":\"Dup\",\"Marker\":1}", typeInfo!);

    await Assert.That(result!.GetType()).IsEqualTo(expectedWinner)
      .Because("the lazy nested-member builder must apply the SAME first-wins rule as the eager builder");
  }

  // --- A resolver that THROWS instead of returning null while resolvability is probed -------------

  public interface IThrowingResolutionBase;

  public sealed record HealthyResolutionProbe : IThrowingResolutionBase {
    public int Value { get; init; }
  }

  public sealed record ThrowingResolutionProbe : IThrowingResolutionBase;

  /// <summary>Hand-rolled resolver that THROWS instead of returning null for one specific type — a
  /// real shape some hand-written or third-party resolvers take.</summary>
  private sealed class _throwOnResolveResolver : IJsonTypeInfoResolver {
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) {
      if (type == typeof(ThrowingResolutionProbe)) {
        throw new NotSupportedException("simulated: this resolver cannot describe ThrowingResolutionProbe");
      }
      return null;
    }
  }

  /// <summary>
  /// A resolver that throws <see cref="NotSupportedException"/> instead of returning null for one
  /// registered derived type must not take down the whole polymorphic base. If the resolvability
  /// probe let that exception escape, one misbehaving resolver anywhere in the combined chain would
  /// fail EVERY polymorphic base sharing that chain, not just the one type it could not describe.
  /// </summary>
  [Test]
  public async Task GetLazyPolymorphicListTypeInfo_DerivedTypeWhoseResolverThrows_IsExcludedWithoutPropagatingAsync() {
    JsonContextRegistry.RegisterContext(HealthyResolutionProbeJsonContext.Default);
    JsonContextRegistry.RegisterContext(new _throwOnResolveResolver());
    JsonContextRegistry.RegisterDerivedType<IThrowingResolutionBase, HealthyResolutionProbe>("Healthy");
    JsonContextRegistry.RegisterDerivedType<IThrowingResolutionBase, ThrowingResolutionProbe>("Throwing");

    var options = JsonContextRegistry.CreateCombinedOptions();

    var listTypeInfo = JsonContextRegistry.GetLazyPolymorphicListTypeInfo<IThrowingResolutionBase>(options);

    await Assert.That(listTypeInfo).IsNotNull()
      .Because("one resolver throwing for an unrelated type must not stop the base typeinfo from building at all");
    await Assert.That(JsonContextRegistry.QuarantinedDerivedTypes.Any(q => q.DerivedType == typeof(ThrowingResolutionProbe))).IsFalse()
      .Because("a type that never resolves is a DIFFERENT failure than one that resolves but fails to configure — quarantine must name only the latter");

    var baseTypeInfo = JsonContextRegistry.GetLazyPolymorphicTypeInfo<IThrowingResolutionBase>(options);
    var json = JsonSerializer.Serialize<IThrowingResolutionBase>(new HealthyResolutionProbe { Value = 9 }, baseTypeInfo!);

    await Assert.That(json).Contains("\"Value\":9")
      .Because("the healthy sibling must keep serializing even though another registered type's resolver throws");
  }

  // --- A composite whose OWN nested members exercise the trial-thread branch -----------------------

  public interface ITrialCompositeBase;

  public sealed record TrialCompositeProbe : ITrialCompositeBase {
    public List<IMessage> Inner { get; init; } = [];
    public MessageEnvelope<IMessage>? Wrapped { get; init; }
  }

  /// <summary>
  /// Resolver for <see cref="TrialCompositeProbe"/> whose property metadata reaches BACK into
  /// <see cref="JsonContextRegistry.GetLazyPolymorphicListTypeInfo{TBase}"/> and
  /// <see cref="JsonContextRegistry.GetLazyPolymorphicEnvelopeTypeInfo{TBase}"/> for its own nested
  /// <c>IMessage</c> members — exactly what a generated context does for a composite's inner-event
  /// list. When THIS type is itself being trial-configured (see the quarantine mechanism), those
  /// calls run with the trial flag set. Captures <c>options</c> from the enclosing
  /// <see cref="GetTypeInfo"/> call (never <c>ctx.Options</c>) — the same safe pattern
  /// <c>JsonContextRegistry</c>'s own polymorphic builders use.
  /// </summary>
  private sealed class _trialCompositeResolver : IJsonTypeInfoResolver {
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) {
      if (type != typeof(TrialCompositeProbe)) {
        return null;
      }
      return JsonMetadataServices.CreateObjectInfo<TrialCompositeProbe>(options, new JsonObjectInfoValues<TrialCompositeProbe> {
        ObjectCreator = () => new TrialCompositeProbe(),
        PropertyMetadataInitializer = _ => [
          JsonMetadataServices.CreatePropertyInfo<List<IMessage>>(options, new JsonPropertyInfoValues<List<IMessage>> {
            IsProperty = true,
            DeclaringType = typeof(TrialCompositeProbe),
            PropertyName = nameof(TrialCompositeProbe.Inner),
            Getter = o => ((TrialCompositeProbe)o!).Inner,
            PropertyTypeInfo = JsonContextRegistry.GetLazyPolymorphicListTypeInfo<IMessage>(options)!,
          }),
          JsonMetadataServices.CreatePropertyInfo<MessageEnvelope<IMessage>>(options, new JsonPropertyInfoValues<MessageEnvelope<IMessage>> {
            IsProperty = true,
            DeclaringType = typeof(TrialCompositeProbe),
            PropertyName = nameof(TrialCompositeProbe.Wrapped),
            Getter = o => ((TrialCompositeProbe)o!).Wrapped,
            PropertyTypeInfo = JsonContextRegistry.GetLazyPolymorphicEnvelopeTypeInfo<IMessage>(options)!,
          }),
        ],
        SerializeHandler = null,
      });
    }
  }

  /// <summary>
  /// A composite whose OWN nested members are a <c>List&lt;IMessage&gt;</c> and a
  /// <c>MessageEnvelope&lt;IMessage&gt;</c> — resolved via the SAME lazy accessors the trial-configure
  /// mechanism uses to validate every OTHER candidate — must configure without hanging or corrupting
  /// shared state. A naive implementation could deadlock (the trial thread re-entering a lock the
  /// caller thread holds) or leak a scratch-bound placeholder into the shared cache, breaking every
  /// OTHER composite's nested list/envelope resolution the moment two composites configure back to
  /// back.
  /// </summary>
  [Test]
  public async Task GetLazyPolymorphicTypeInfo_CompositeWithNestedListAndEnvelopeMembers_ConfiguresWithoutThrowingAsync() {
    JsonContextRegistry.RegisterContext(new _trialCompositeResolver());
    JsonContextRegistry.RegisterDerivedType<ITrialCompositeBase, TrialCompositeProbe>("TrialComposite");

    var options = JsonContextRegistry.CreateCombinedOptions();

    var baseTypeInfo = JsonContextRegistry.GetLazyPolymorphicTypeInfo<ITrialCompositeBase>(options);

    await Assert.That(baseTypeInfo).IsNotNull()
      .Because("a composite whose own nested members route through the trial-safe lazy accessors must still produce a usable base typeinfo");
  }

  // --- The list metadata's ObjectCreator must actually run, not just exist -------------------------

  public interface IListMaterializationBase;

  public sealed record ListMaterializationChild : IListMaterializationBase;

  /// <summary>
  /// The polymorphic list metadata's <c>ObjectCreator</c> is what allocates the runtime
  /// <c>List&lt;T&gt;</c> during deserialization — building the <c>JsonTypeInfo</c> alone never calls
  /// it. If it were ever missing or wrong, every deserialize of a nested inner-message list (a
  /// composite's <c>InnerEvents</c>, for instance) would throw the moment a consumer actually read one
  /// off the wire, even though constructing the options at startup looked completely fine.
  /// </summary>
  [Test]
  public async Task GetLazyPolymorphicListTypeInfo_DeserializingAnEmptyArray_MaterializesViaObjectCreatorAsync() {
    // The derived type needs a resolver as well as a registration: a registered type no resolver
    // can supply is filtered out of the polymorphic options, and a base left with zero derived
    // types makes System.Text.Json throw when the list metadata is built.
    JsonContextRegistry.RegisterContext(ListMaterializationJsonContext.Default);
    JsonContextRegistry.RegisterDerivedType<IListMaterializationBase, ListMaterializationChild>("ListMaterializationChild");
    var options = JsonContextRegistry.CreateCombinedOptions();
    var listTypeInfo = JsonContextRegistry.GetLazyPolymorphicListTypeInfo<IListMaterializationBase>(options);

    var result = JsonSerializer.Deserialize("[]", listTypeInfo!);

    await Assert.That(result).IsNotNull();
    await Assert.That(result!).IsEmpty();
  }
}

/// <summary>JSON context supplying the derived type for the list-materialization test.</summary>
[JsonSerializable(typeof(JsonContextRegistryCoverageTests.ListMaterializationChild))]
public sealed partial class ListMaterializationJsonContext : JsonSerializerContext;

/// <summary>JSON context backing the duplicate-discriminator determinism tests.</summary>
[JsonSerializable(typeof(JsonContextRegistryCoverageTests.DuplicateDiscriminatorFirst))]
[JsonSerializable(typeof(JsonContextRegistryCoverageTests.DuplicateDiscriminatorSecond))]
public sealed partial class DuplicateDiscriminatorJsonContext : JsonSerializerContext;

/// <summary>JSON context for the healthy sibling in the throwing-resolver test.</summary>
[JsonSerializable(typeof(JsonContextRegistryCoverageTests.HealthyResolutionProbe))]
public sealed partial class HealthyResolutionProbeJsonContext : JsonSerializerContext;
