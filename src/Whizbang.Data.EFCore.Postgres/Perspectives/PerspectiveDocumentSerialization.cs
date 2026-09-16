using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Whizbang.Core.Serialization;

namespace Whizbang.Data.EFCore.Postgres.Perspectives;

/// <summary>
/// The one set of serializer options every perspective document is written and read with.
/// </summary>
/// <remarks>
/// <para>
/// A perspective document is written by the atomic upsert, which serializes it with the persistence
/// profile and sends it as a parameter. A document stored as one value is read back by Entity
/// Framework, and the options Entity Framework would reach for on its own are the data source's,
/// which are the default profile: the outbox, inbox and event store metadata columns read through
/// them in the wire's form, and they cannot change. So a document column is bound to these options
/// explicitly, through <see cref="ConverterFor{T}"/>, and the writer and the reader are one set of
/// options rather than two that have to agree.
/// </para>
/// <para>
/// Built as the upsert has always built them: the cross-assembly union under the persistence
/// profile, a caller-supplied resolver as a fallback, and the registered modifiers re-applied over
/// the finished chain. Reused until the registry changes, because a set of options carries the
/// serializer's metadata cache and rebuilding per call throws it away; rebuilt when the registry
/// changes, because an assembly loaded late registers its contexts late and a set built before
/// that cannot resolve what it brought.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveDocumentSerializationTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OpaqueDocumentRoundTripTests.cs</tests>
public static class PerspectiveDocumentSerialization {
  private static readonly Lock _gate = new();
  private static long _builtForGeneration = -1;
  private static Func<JsonSerializerOptions>? _builtForProvider;
  private static JsonSerializerOptions? _built;

  /// <summary>
  /// The options for every perspective document, folding in the atomic upsert's caller-supplied
  /// provider when one is configured.
  /// </summary>
  public static JsonSerializerOptions Options => Resolve(BaseUpsertStrategy.PathOnePersistenceOptionsProvider);

  /// <summary>
  /// The options for a perspective document, with a caller-supplied provider folded in behind the
  /// profile's union.
  /// </summary>
  /// <param name="userProvider">The caller's options, or <see langword="null"/> for the union alone.</param>
  /// <returns>Options reused across calls until the registry or the provider changes.</returns>
  public static JsonSerializerOptions Resolve(Func<JsonSerializerOptions>? userProvider) {
    var generation = JsonContextRegistry.Generation;
    lock (_gate) {
      if (_built is not null && _builtForGeneration == generation && ReferenceEquals(_builtForProvider, userProvider)) {
        return _built;
      }

      _built = _build(userProvider);
      _builtForGeneration = generation;
      _builtForProvider = userProvider;
      return _built;
    }
  }

  /// <summary>
  /// A converter that stores a document of this type as text under the persistence profile, for a
  /// jsonb column Entity Framework reads as one value.
  /// </summary>
  /// <typeparam name="T">The document type: a perspective model, its metadata or its scope.</typeparam>
  /// <returns>The converter, resolving the type's metadata through <see cref="Options"/> on each use.</returns>
  /// <remarks>
  /// Resolves on each use rather than capturing the options once, so a document written after a
  /// late registration is written with options that know about it.
  /// </remarks>
  public static ValueConverter<T, string> ConverterFor<T>() where T : class =>
    new(document => Serialize(document), stored => Deserialize<T>(stored));

  /// <summary>Serializes a document with <see cref="Options"/>.</summary>
  /// <typeparam name="T">The document type.</typeparam>
  /// <param name="document">The document.</param>
  /// <returns>The document as text.</returns>
  public static string Serialize<T>(T document) where T : class =>
    JsonSerializer.Serialize(document, _typeInfo<T>());

  /// <summary>Deserializes a document with <see cref="Options"/>.</summary>
  /// <typeparam name="T">The document type.</typeparam>
  /// <param name="stored">The document as text.</param>
  /// <returns>The document.</returns>
  public static T Deserialize<T>(string stored) where T : class =>
    JsonSerializer.Deserialize(stored, _typeInfo<T>())
      ?? throw new JsonException($"A stored {typeof(T).Name} document was null, which no document is");

  private static JsonTypeInfo<T> _typeInfo<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));

  private static JsonSerializerOptions _build(Func<JsonSerializerOptions>? userProvider) {
    var union = JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Persistence);
    var user = userProvider?.Invoke();
    if (user?.TypeInfoResolver is null) {
      return union;
    }

    // Union first (object-mode WhizbangId + all registered persistence contexts), user options as a
    // fallback for anything the union doesn't cover. User converters are intentionally NOT copied:
    // the persistence profile deliberately omits the scalar WhizbangId converters so object-mode
    // wins.
    //
    // The finished chain is re-wrapped with the registered modifiers. The union already carries
    // them, but they were attached to the union's own resolver: a type the user's resolver is the
    // first to answer for would otherwise be serialized without them.
    var chain = JsonTypeInfoResolver.Combine(union.TypeInfoResolver!, user.TypeInfoResolver);

    return new JsonSerializerOptions(union) {
      TypeInfoResolver = JsonContextRegistry.WithRegisteredModifiers(chain, SerializationProfile.Persistence),
    };
  }
}
