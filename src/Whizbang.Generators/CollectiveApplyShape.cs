// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// The one definition of a collective-event handler the framework binds: a method carrying
/// <c>[CollectiveApplyFor]</c> that returns <c>ICollectiveSpec&lt;TModel&gt;</c> and takes the collective event,
/// optionally followed by the <c>ICollectiveQuery</c> context. The handler is bound by this shape, not by an
/// interface, so every reader of the shape uses this one test: the discovery generator that invokes it, the
/// registry that lists its event type, and the suppressor that knows which parameter the framework requires.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Generators.Tests/CollectiveApplyDiscoveryGeneratorTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveRunnerRegistryGeneratorTests.cs:Generator_AllEventTypes_LeavesOutAnAttributedMethodTheCollectiveGeneratorRejectsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Suppressors/ConventionBoundParameterSuppressorTests.cs</tests>
internal static class CollectiveApplyShape {
  internal const string ATTRIBUTE_FQN = "Whizbang.Core.Perspectives.CollectiveApplyForAttribute";
  private const string COLLECTIVE_EVENT_FQN = "Whizbang.Core.Messaging.ICollectiveEvent";
  private const string COLLECTIVE_QUERY_FQN = "Whizbang.Core.Perspectives.ICollectiveQuery";
  private const string SPEC_TYPE_NAME = "ICollectiveSpec";
  private const string SPEC_NAMESPACE = "Whizbang.Core.Perspectives";

  /// <summary>The <c>[CollectiveApplyFor]</c> attribute on <paramref name="method"/>, or null.</summary>
  internal static AttributeData? Attribute(IMethodSymbol method) =>
    method.GetAttributes().FirstOrDefault(a => TypeNameUtilities.IsNamed(a.AttributeClass, ATTRIBUTE_FQN));

  /// <summary>
  /// The model the handler's spec mutates when <paramref name="method"/> is a handler the framework binds, or
  /// null when it is not (no attribute, another return type, or parameters the generator does not accept).
  /// </summary>
  internal static ITypeSymbol? BoundModel(IMethodSymbol method) =>
    Attribute(method) is not null && _hasCollectiveParameters(method) ? _specModelType(method) : null;

  /// <summary>
  /// The TModel of an <c>ICollectiveSpec&lt;TModel&gt;</c> return type, or null for any other shape. Other shapes
  /// are rejected silently so a misuse of the attribute doesn't crash the generator.
  /// </summary>
  private static ITypeSymbol? _specModelType(IMethodSymbol methodSymbol) {
    if (methodSymbol.ReturnType is not INamedTypeSymbol returnType ||
        !returnType.IsGenericType ||
        returnType.OriginalDefinition.Name != SPEC_TYPE_NAME ||
        !TypeNameUtilities.IsNamed(returnType.OriginalDefinition.ContainingNamespace, SPEC_NAMESPACE)) {
      return null;
    }

    return returnType.TypeArguments.Length == 1 ? returnType.TypeArguments[0] : null;
  }

  /// <summary>
  /// First parameter implements ICollectiveEvent; an optional second parameter is the ICollectiveQuery context
  /// (handlers that scope by a sibling perspective take it; others omit it).
  /// </summary>
  private static bool _hasCollectiveParameters(IMethodSymbol methodSymbol) {
    if (methodSymbol.Parameters.Length is < 1 or > 2) {
      return false;
    }
    var eventType = methodSymbol.Parameters[0].Type;
    if (!eventType.AllInterfaces.Any(i => TypeNameUtilities.IsNamed(i, COLLECTIVE_EVENT_FQN))) {
      return false;
    }
    if (methodSymbol.Parameters.Length == 1) {
      return true;
    }

    var queryParam = methodSymbol.Parameters[1].Type;
    return TypeNameUtilities.IsNamed(queryParam, COLLECTIVE_QUERY_FQN)
      || queryParam.AllInterfaces.Any(i => TypeNameUtilities.IsNamed(i, COLLECTIVE_QUERY_FQN));
  }
}
