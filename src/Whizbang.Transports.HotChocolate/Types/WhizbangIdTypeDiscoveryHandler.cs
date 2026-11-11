// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using HotChocolate.Internal;
using HotChocolate.Types;
using HotChocolate.Types.Descriptors;
using Whizbang.Core;

namespace Whizbang.Transports.HotChocolate.Types;

/// <summary>
/// Gives a <c>[WhizbangId]</c> type its <see cref="WhizbangIdType"/> scalar when the schema first refers to it, as an
/// argument, an input field or a result. A schema therefore gets a scalar only for the ids it uses: the framework's
/// own ids (<c>StreamId</c>, <c>EventId</c>, <c>MessageId</c>, <c>CorrelationId</c>) add nothing to a schema that does
/// not use them, and a consumer id with one of those names does not collide with them.
/// </summary>
/// <remarks>
/// The id's conversion comes from <see cref="WhizbangIdProviderRegistry"/>, which the generated module initializer of
/// the id's assembly fills. The handler runs that initializer itself before it reads the registry, so the scalar does
/// not depend on any code in the id's assembly having run before the schema is built. A type that implements
/// <see cref="IWhizbangId"/> with no recorded conversion (one not generated from <c>[WhizbangId]</c>) is left to
/// HotChocolate's own handling. One handler serves one schema build and keeps one scalar per id type.
/// </remarks>
/// <docs>apis/graphql/setup</docs>
/// <tests>tests/Whizbang.Transports.HotChocolate.Ids.Tests/Unit/WhizbangIdSchemaRegistrationTests.cs</tests>
/// <tests>tests/Whizbang.Transports.HotChocolate.Ids.Tests/Unit/WhizbangIdSchemaTimingTests.cs</tests>
internal sealed class WhizbangIdTypeDiscoveryHandler : TypeDiscoveryHandler {
  private readonly Dictionary<Type, WhizbangIdType?> _scalars = [];

  /// <inheritdoc />
  public override bool TryInferType(
      TypeReference typeReference,
      TypeDiscoveryInfo typeInfo,
      [NotNullWhen(true)] out TypeReference[]? schemaTypeRefs) {
    var scalar = _scalarOf(typeInfo.RuntimeType);
    schemaTypeRefs = scalar is null ? null : [TypeReference.Create(scalar)];
    return scalar is not null;
  }

  /// <inheritdoc />
  public override bool TryInferKind(TypeReference typeReference, TypeDiscoveryInfo typeInfo, out TypeKind typeKind) {
    var isId = _scalarOf(typeInfo.RuntimeType) is not null;
    typeKind = isId ? TypeKind.Scalar : default;
    return isId;
  }

  private WhizbangIdType? _scalarOf(Type runtimeType) {
    if (!typeof(IWhizbangId).IsAssignableFrom(runtimeType)) {
      return null;
    }

    if (!_scalars.TryGetValue(runtimeType, out var scalar)) {
      scalar = _createScalar(runtimeType);
      _scalars[runtimeType] = scalar;
    }

    return scalar;
  }

  private static WhizbangIdType? _createScalar(Type idType) =>
    ConversionOf(idType) is { } fromGuid ? new WhizbangIdType(idType, fromGuid) : null;

  /// <summary>
  /// The Guid conversion of a <c>[WhizbangId]</c> type, or null for a type that is not one or has none recorded.
  /// </summary>
  /// <param name="type">A runtime type the schema refers to.</param>
  /// <returns>The id's <c>From(Guid)</c>, or null.</returns>
  internal static Func<Guid, object>? ConversionOf(Type type) {
    if (!typeof(IWhizbangId).IsAssignableFrom(type)) {
      return null;
    }

    // A module initializer runs the first time its module is used; run it now, so the registry has the id's
    // conversion however little of its assembly has run so far. It runs at most once.
    RuntimeHelpers.RunModuleConstructor(type.Module.ModuleHandle);

    return WhizbangIdProviderRegistry.GetGuidConversions().GetValueOrDefault(type);
  }
}
