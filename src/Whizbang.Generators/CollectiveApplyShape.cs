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
/// registry that lists its event type, the suppressor that knows which parameter the framework requires, and the
/// analyzer that reports a method meant to be a handler that does not match (WHIZ116).
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Generators.Tests/CollectiveApplyDiscoveryGeneratorTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveRunnerRegistryGeneratorTests.cs:Generator_AllEventTypes_LeavesOutAnAttributedMethodTheCollectiveGeneratorRejectsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/Suppressors/ConventionBoundParameterSuppressorTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/CollectiveApplyShapeAnalyzerTests.cs</tests>
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
    Attribute(method) is not null && Mismatch(method) is null ? _specModelType(method) : null;

  /// <summary>
  /// Why <paramref name="method"/>'s signature is not a collective handler's, or null when it is: the reason WHIZ116
  /// reports for a method meant to be one. The attribute is not part of the answer; the caller asks about it.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/CollectiveApplyShapeAnalyzerTests.cs:AnAttributedMethodThatDoesNotMatchTheShape_IsAnErrorNamingWhatIsWrongAsync</tests>
  internal static string? Mismatch(IMethodSymbol method) {
    var parameters = method.Parameters;
    if (parameters.Length is < 1 or > 2) {
      return $"it takes {parameters.Length} parameters, and a collective Apply takes the collective event, optionally followed by the query context.";
    }
    if (!parameters[0].Type.AllInterfaces.Any(static i => TypeNameUtilities.IsNamed(i, COLLECTIVE_EVENT_FQN))) {
      return $"its first parameter, '{TypeNameUtilities.MinimallyQualified(parameters[0].Type)}', is not an ICollectiveEvent.";
    }
    if (parameters.Length == 2 && !_isCollectiveQuery(parameters[1].Type)) {
      return $"its second parameter, '{TypeNameUtilities.MinimallyQualified(parameters[1].Type)}', is not an ICollectiveQuery.";
    }
    return _specModelType(method) is null
      ? $"it returns '{TypeNameUtilities.MinimallyQualified(method.ReturnType)}', not ICollectiveSpec<TModel>."
      : null;
  }

  /// <summary>
  /// The TModel of an <c>ICollectiveSpec&lt;TModel&gt;</c> return type, or null for any other shape. The generator
  /// leaves another shape out rather than failing on it, and WHIZ116 reports it.
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

  /// <summary>The optional second parameter: the ICollectiveQuery context, or a type that implements it.</summary>
  private static bool _isCollectiveQuery(ITypeSymbol type) =>
    TypeNameUtilities.IsNamed(type, COLLECTIVE_QUERY_FQN)
    || type.AllInterfaces.Any(static i => TypeNameUtilities.IsNamed(i, COLLECTIVE_QUERY_FQN));
}
