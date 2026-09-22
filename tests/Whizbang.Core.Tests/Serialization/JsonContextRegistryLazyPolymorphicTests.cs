using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Serialization;

/// <summary>
/// Covers the lazy polymorphic accessors on <see cref="JsonContextRegistry"/> — the
/// list and envelope variants, which had no tests at all.
/// </summary>
/// <remarks>
/// Shares the "JsonContextRegistryMutation" not-in-parallel group with
/// <see cref="PolymorphicQuarantineTests"/>: both call RegisterDerivedType, which
/// mutates a process-wide registry. A distinct key would let the two classes register
/// concurrently and see each other's derived types.
/// </remarks>
[NotInParallel("JsonContextRegistryMutation")]
[Category("Core")]
[Category("Serialization")]
public class JsonContextRegistryLazyPolymorphicTests {

  /// <summary>A base with no registered derived types, for the not-polymorphic path.</summary>
  public abstract record UnregisteredBase;

  /// <summary>A base registered below, for the resolved path.</summary>
  public abstract record RegisteredBase;

  public sealed record RegisteredChild : RegisteredBase {
    public string Name { get; init; } = string.Empty;
  }

  // --- Null-argument guards --------------------------------------------------

  [Test]
  public async Task GetLazyPolymorphicTypeInfo_WithNullOptions_ThrowsAsync() {
    await Assert.That(() => JsonContextRegistry.GetLazyPolymorphicTypeInfo<RegisteredBase>(null!))
        .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task GetLazyPolymorphicListTypeInfo_WithNullOptions_ThrowsAsync() {
    await Assert.That(() => JsonContextRegistry.GetLazyPolymorphicListTypeInfo<RegisteredBase>(null!))
        .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task GetLazyPolymorphicEnvelopeTypeInfo_WithNullOptions_ThrowsAsync() {
    await Assert.That(() => JsonContextRegistry.GetLazyPolymorphicEnvelopeTypeInfo<RegisteredBase>(null!))
        .ThrowsExactly<ArgumentNullException>();
  }

  // --- No derived types registered -------------------------------------------

  [Test]
  public async Task GetLazyPolymorphicTypeInfo_ForUnregisteredBase_ReturnsNullAsync() {
    // Null means "not polymorphic here", so the caller falls back to normal metadata
    // rather than treating the absence as an error.
    var options = JsonContextRegistry.CreateCombinedOptions();

    var info = JsonContextRegistry.GetLazyPolymorphicTypeInfo<UnregisteredBase>(options);

    await Assert.That(info).IsNull();
  }

  [Test]
  public async Task GetLazyPolymorphicListTypeInfo_ForUnregisteredBase_ReturnsNullAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();

    var info = JsonContextRegistry.GetLazyPolymorphicListTypeInfo<UnregisteredBase>(options);

    await Assert.That(info).IsNull();
  }

  [Test]
  public async Task GetLazyPolymorphicEnvelopeTypeInfo_ForUnregisteredBase_ReturnsNullAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();

    var info = JsonContextRegistry.GetLazyPolymorphicEnvelopeTypeInfo<UnregisteredBase>(options);

    await Assert.That(info).IsNull();
  }

  // --- Derived type registered -----------------------------------------------

  [Test]
  public async Task GetLazyPolymorphicListTypeInfo_ForRegisteredBase_BuildsListMetadataAsync() {
    JsonContextRegistry.RegisterDerivedType<RegisteredBase, RegisteredChild>();
    var options = JsonContextRegistry.CreateCombinedOptions();

    var info = JsonContextRegistry.GetLazyPolymorphicListTypeInfo<RegisteredBase>(options);

    await Assert.That(info).IsNotNull();
    await Assert.That(info!.Type).IsEqualTo(typeof(List<RegisteredBase>));
  }

  [Test]
  public async Task GetLazyPolymorphicEnvelopeTypeInfo_ForRegisteredBase_BuildsEnvelopeMetadataAsync() {
    JsonContextRegistry.RegisterDerivedType<RegisteredBase, RegisteredChild>();
    var options = JsonContextRegistry.CreateCombinedOptions();

    var info = JsonContextRegistry.GetLazyPolymorphicEnvelopeTypeInfo<RegisteredBase>(options);

    await Assert.That(info).IsNotNull();
    await Assert.That(info!.Type).IsEqualTo(typeof(MessageEnvelope<RegisteredBase>));
  }

  [Test]
  public async Task GetLazyPolymorphicListTypeInfo_CalledTwice_ReusesTheCachedMetadataAsync() {
    // The second call must hit the resolver cache rather than rebuild — rebuilding per
    // call would allocate a fresh typeinfo on every serialize.
    JsonContextRegistry.RegisterDerivedType<RegisteredBase, RegisteredChild>();
    var options = JsonContextRegistry.CreateCombinedOptions();

    var first = JsonContextRegistry.GetLazyPolymorphicListTypeInfo<RegisteredBase>(options);
    var second = JsonContextRegistry.GetLazyPolymorphicListTypeInfo<RegisteredBase>(options);

    await Assert.That(first).IsSameReferenceAs(second);
  }

  [Test]
  public async Task GetLazyPolymorphicEnvelopeTypeInfo_CalledTwice_ReusesTheCachedMetadataAsync() {
    JsonContextRegistry.RegisterDerivedType<RegisteredBase, RegisteredChild>();
    var options = JsonContextRegistry.CreateCombinedOptions();

    var first = JsonContextRegistry.GetLazyPolymorphicEnvelopeTypeInfo<RegisteredBase>(options);
    var second = JsonContextRegistry.GetLazyPolymorphicEnvelopeTypeInfo<RegisteredBase>(options);

    await Assert.That(first).IsSameReferenceAs(second);
  }

  [Test]
  public async Task RegisterDerivedType_IsIdempotentAsync() {
    JsonContextRegistry.RegisterDerivedType<RegisteredBase, RegisteredChild>();
    JsonContextRegistry.RegisterDerivedType<RegisteredBase, RegisteredChild>();

    var derived = JsonContextRegistry.GetRegisteredDerivedTypes<RegisteredBase>().ToList();

    await Assert.That(derived.Count(t => t == typeof(RegisteredChild))).IsEqualTo(1);
  }
}
