// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The dictionary types perspective documents hold, each with its stored form (#1911).
/// </summary>
/// <remarks>
/// <para>
/// Filled by generated code: every assembly's perspective schema registration calls <see cref="Register{TKey, TValue}"/>
/// from its module initializer for each closed dictionary type a perspective model holds, at any depth (a member, a
/// member of a nested object or of a collection element, a dictionary's own value). The author declares a plain
/// dictionary and nothing else.
/// </para>
/// <para>
/// Read by both writers of a document: the persistence profile's serializer options resolve a registered type to
/// <see cref="KeyValueArrayJsonConverter{TMember, TKey, TValue}"/>, and Entity Framework's type mapping source maps it
/// to the same form. A dictionary type nobody registered is neither: the serializer writes it as an object, and Entity
/// Framework refuses the model, naming the member.
/// </para>
/// <para>
/// Scoped to the persistence profile, the perspective-document profile. Events and the wire keep a dictionary as a
/// JSON object.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/dictionary-members</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/DictionaryMemberFormsTests.cs</tests>
public static class DictionaryMemberForms {
  /// <summary>
  /// The resolver's priority on the persistence profile: above every generated context (the persistence context is
  /// 1000), so a registered dictionary is never resolved to the object form a context would build.
  /// </summary>
  public const int RESOLVER_PRIORITY = 2000;

  private static readonly ConcurrentDictionary<Type, DictionaryMemberForm> _byMemberType = new();
  private static int _resolverRegistered;

  /// <summary>Registers the stored form of a dictionary of <typeparamref name="TKey"/> to <typeparamref name="TValue"/>.</summary>
  /// <typeparam name="TKey">The key type.</typeparam>
  /// <typeparam name="TValue">The value type.</typeparam>
  /// <remarks>Idempotent: two assemblies registering one type register it once.</remarks>
  public static void Register<TKey, TValue>() where TKey : notnull {
    var form = DictionaryMemberForm<TKey, TValue>.Instance;
    foreach (var memberType in form.MemberTypes) {
      _byMemberType.TryAdd(memberType, form);
    }

    // One resolver for every form, consulting the live set: a form registered later (an assembly loaded late) is
    // answered by the same resolver, and registering it once keeps the profile's chain from growing per type.
    if (Interlocked.Exchange(ref _resolverRegistered, 1) == 0) {
      JsonContextRegistry.RegisterContext(Resolver.Instance, RESOLVER_PRIORITY, SerializationProfile.Persistence);
      // And on the converter list, which a context consults for a member's type before its own metadata.
      JsonContextRegistry.RegisterConverter(KeyValueArrayJsonConverterFactory.Instance, RESOLVER_PRIORITY, SerializationProfile.Persistence);
    }
  }

  /// <summary>The form registered for a member type, if any.</summary>
  /// <param name="memberType">A dictionary member's declared type.</param>
  /// <param name="form">The form, when registered.</param>
  /// <returns>Whether the type is registered.</returns>
  public static bool TryGet(Type memberType, [global::System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out DictionaryMemberForm? form) {
    ArgumentNullException.ThrowIfNull(memberType);
    return _byMemberType.TryGetValue(memberType, out form);
  }

  /// <summary>Every registered form, once each.</summary>
  public static IReadOnlyCollection<DictionaryMemberForm> Registered => [.. _byMemberType.Values.Distinct()];

  /// <summary>Resolves a registered dictionary type to its stored form on the persistence profile.</summary>
  private sealed class Resolver : IJsonTypeInfoResolver {
    public static Resolver Instance { get; } = new();

    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) =>
      _byMemberType.TryGetValue(type, out var form) ? form.CreateTypeInfo(type, options) : null;
  }
}
