// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Attributes;

namespace Whizbang.Transports.FastEndpoints;

/// <summary>
/// Masks <see cref="FieldPermissionAttribute"/> members when a REST response is written. The REST lens
/// generator registers every protected member of a lens's model, and of the types nested inside it; the
/// JSON type info modifier then writes the value <see cref="FieldPermissionMasking"/> produces for a caller
/// whose scope lacks the permission.
/// </summary>
/// <remarks>
/// <para>
/// <c>AddWhizbangLenses()</c> adds the modifier to the host's JSON options, which FastEndpoints writes
/// responses with. A protected string member under Mask, Partial or Redact is written as its placeholder. A
/// member that is hidden (Hide, an undefined strategy, or any non-string member) is written as null, or left
/// out of the response when its type cannot hold null (an <c>int</c>, a <c>decimal</c>, a struct).
/// </para>
/// <para>
/// The caller is read from <see cref="ScopeContextAccessor.CurrentContext"/>; a request with no scope is never
/// permitted. Registration comes from generated code and the modifier matches members by their JSON name, so
/// nothing here reflects over a model: it works with source-generated serializer contexts and under trimming.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/security#column-level-security</docs>
/// <tests>tests/Whizbang.Transports.FastEndpoints.Tests/Unit/FieldPermissionJsonTests.cs</tests>
/// <tests>tests/Whizbang.Transports.FastEndpoints.Integration.Tests/RestLens/FieldPermissionRestLensTests.cs</tests>
public static class FieldPermissionJson {
  private static readonly Lock _gate = new();
  private static readonly Dictionary<Type, Dictionary<string, ProtectedMember>> _members = [];
  private static readonly ConditionalWeakTable<JsonSerializerOptions, object> _maskingOptions = [];
  private static readonly object _marker = new();

  /// <summary>A registered protected member.</summary>
  private sealed record ProtectedMember(string MemberName, string? JsonName, FieldPermissionAttribute Attribute, bool IsString);

  /// <summary>
  /// Registers a protected member. Generated code calls this when the assembly that declares the lens loads.
  /// Registering a member again replaces its earlier registration.
  /// </summary>
  /// <param name="declaringType">The type that declares the member.</param>
  /// <param name="memberName">The member's name.</param>
  /// <param name="jsonName">The member's explicit JSON name, or <see langword="null"/> to apply the serializer's naming policy to <paramref name="memberName"/>.</param>
  /// <param name="attribute">The member's field permission.</param>
  /// <param name="isString">Whether the member is declared as a string.</param>
  public static void Register(Type declaringType, string memberName, string? jsonName, FieldPermissionAttribute attribute, bool isString) {
    ArgumentNullException.ThrowIfNull(declaringType);
    ArgumentNullException.ThrowIfNull(memberName);
    ArgumentNullException.ThrowIfNull(attribute);

    lock (_gate) {
      if (!_members.TryGetValue(declaringType, out var byName)) {
        byName = new Dictionary<string, ProtectedMember>(StringComparer.Ordinal);
        _members.Add(declaringType, byName);
      }
      byName[memberName] = new ProtectedMember(memberName, jsonName, attribute, isString);
    }
  }

  /// <summary>
  /// The JSON type info modifier: masks the registered members of the type, including members it inherits.
  /// </summary>
  /// <param name="typeInfo">The contract being built.</param>
  public static void Modify(JsonTypeInfo typeInfo) {
    ArgumentNullException.ThrowIfNull(typeInfo);
    _maskingOptions.AddOrUpdate(typeInfo.Options, _marker);
    if (typeInfo.Kind != JsonTypeInfoKind.Object) {
      return;
    }

    foreach (var member in _protectedMembersOf(typeInfo.Type)) {
      var name = member.JsonName ?? typeInfo.Options.PropertyNamingPolicy?.ConvertName(member.MemberName) ?? member.MemberName;
      foreach (var property in typeInfo.Properties.Where(p => p.Name == name)) {
        _protect(property, member);
      }
    }
  }

  /// <summary>
  /// Adds the masking to <paramref name="options"/>, keeping its current contract resolver.
  /// </summary>
  /// <param name="options">Options that are not yet in use, with a contract resolver.</param>
  /// <returns>The same options.</returns>
  /// <exception cref="InvalidOperationException">The options have no contract resolver to add the masking to.</exception>
  public static JsonSerializerOptions AddWhizbangFieldPermissions(this JsonSerializerOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    // Options with no resolver would fall back to reflection when first used, which the masking cannot wrap
    // without depending on reflection itself. The host's JSON options always carry a resolver.
    var resolver = options.TypeInfoResolver ?? throw new InvalidOperationException(
      "The JSON options have no TypeInfoResolver to add the field masking to. Set one (a source-generated " +
      "JsonSerializerContext, or the host's default) before calling AddWhizbangFieldPermissions().");
    options.TypeInfoResolver = resolver.WithAddedModifier(Modify);
    return options;
  }

  /// <summary>
  /// Fails unless the options FastEndpoints writes responses with mask protected members. A generated lens
  /// endpoint whose model has protected members calls this before it queries, so it never sends them unmasked.
  /// </summary>
  /// <param name="modelType">The lens's model type.</param>
  /// <exception cref="InvalidOperationException">The serializer options do not mask protected members.</exception>
  public static void EnsureMasked(Type modelType) =>
    EnsureMasked(new global::FastEndpoints.Config().Serializer.Options, modelType);

  /// <summary>
  /// Fails unless <paramref name="options"/> mask protected members.
  /// </summary>
  /// <param name="options">The options a response is written with.</param>
  /// <param name="modelType">The model type the response carries.</param>
  /// <exception cref="InvalidOperationException">The options do not mask protected members.</exception>
  public static void EnsureMasked(JsonSerializerOptions options, Type modelType) {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(modelType);

    // Building the model's contract runs every modifier the options carry; the masking one records the options.
    _ = options.GetTypeInfo(modelType);
    if (!_maskingOptions.TryGetValue(options, out _)) {
      throw new InvalidOperationException(
        $"{modelType.Name} has [FieldPermission] members, but the JSON options the response is written with do not mask them. " +
        "Call services.AddWhizbangLenses() from Whizbang.Transports.FastEndpoints, and when replacing the serializer's " +
        "TypeInfoResolver call AddWhizbangFieldPermissions() on the options afterwards.");
    }
  }

  private static List<ProtectedMember> _protectedMembersOf(Type type) {
    var found = new List<ProtectedMember>();
    lock (_gate) {
      for (var current = type; current is not null; current = current.BaseType) {
        if (_members.TryGetValue(current, out var byName)) {
          found.AddRange(byName.Values);
        }
      }
    }
    return found;
  }

  private static void _protect(JsonPropertyInfo property, ProtectedMember member) {
    var strategy = member.Attribute.Masking;
    if (FieldPermissionMasking.IsWithheld(strategy, member.IsString) && !_canHoldNull(property.PropertyType)) {
      // A hidden value written as null; a type with no null (an int, a struct) cannot be, so it is left out.
      var shouldSerialize = property.ShouldSerialize ?? ((_, _) => true);
      property.ShouldSerialize = (owner, value) => _isPermitted(member) && shouldSerialize(owner, value);
      return;
    }

    // A registered member always has a public getter, so the contract always has one. Replacing the value,
    // rather than the decision to write it, keeps the options' own ignore rules working for every caller.
    var get = property.Get!;
    property.Get = owner => _isPermitted(member)
      ? get(owner)
      : FieldPermissionMasking.MaskValue(get(owner), strategy, member.IsString);
  }

  private static bool _canHoldNull(Type type) => !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

  private static bool _isPermitted(ProtectedMember member) =>
    FieldPermissionMasking.IsPermitted(ScopeContextAccessor.CurrentContext, member.Attribute);
}
