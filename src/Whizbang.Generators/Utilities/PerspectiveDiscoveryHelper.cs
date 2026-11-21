// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Utilities;

/// <summary>
/// Shared discovery logic for finding perspective interfaces on a class.
/// All generators MUST use this helper instead of implementing their own scanning.
/// Handles IPerspectiveFor, IPerspectiveWithActionsFor, IPerspectiveBase, and IGlobalPerspectiveFor.
/// </summary>
internal static class PerspectiveDiscoveryHelper {
  // Interface names without global:: prefix — matches default ToDisplayString() format
  private const string PERSPECTIVE_BASE = "Whizbang.Core.Perspectives.IPerspectiveBase";
  private const string PERSPECTIVE_FOR = "Whizbang.Core.Perspectives.IPerspectiveFor";
  private const string PERSPECTIVE_WITH_ACTIONS_FOR = "Whizbang.Core.Perspectives.IPerspectiveWithActionsFor";
  private const string GLOBAL_PERSPECTIVE_FOR = "Whizbang.Core.Perspectives.IGlobalPerspectiveFor";
  private const string GENERIC_MODEL_EVENT_PREFIX = "<TModel, TEvent";

  /// <summary>
  /// Finds all single-stream perspective interfaces on a class.
  /// Scans IPerspectiveBase, IPerspectiveFor, and IPerspectiveWithActionsFor.
  /// Returns interfaces with TModel as first type arg and TEvent1..N as remaining args.
  /// </summary>
  public static List<INamedTypeSymbol> FindSingleStreamInterfaces(INamedTypeSymbol classSymbol) {
    return [.. classSymbol.AllInterfaces
      .Where(i => {
        var originalDef = TypeNameUtilities.Display(i.OriginalDefinition);
        // Match with "<TModel, TEvent" prefix to skip marker-only bases like IPerspectiveBase<TModel>
        return (originalDef.StartsWith(PERSPECTIVE_BASE + GENERIC_MODEL_EVENT_PREFIX, StringComparison.Ordinal) ||
                originalDef.StartsWith(PERSPECTIVE_FOR + GENERIC_MODEL_EVENT_PREFIX, StringComparison.Ordinal) ||
                originalDef.StartsWith(PERSPECTIVE_WITH_ACTIONS_FOR + GENERIC_MODEL_EVENT_PREFIX, StringComparison.Ordinal))
               && i.TypeArguments.Length >= 2;
      })];
  }

  /// <summary>
  /// Finds all global (multi-stream) perspective interfaces on a class.
  /// </summary>
  public static List<INamedTypeSymbol> FindGlobalInterfaces(INamedTypeSymbol classSymbol) {
    return [.. classSymbol.AllInterfaces
      .Where(i => {
        var originalDef = TypeNameUtilities.Display(i.OriginalDefinition);
        return originalDef.StartsWith(GLOBAL_PERSPECTIVE_FOR + "<", StringComparison.Ordinal)
               && i.TypeArguments.Length >= 3;
      })];
  }


  /// <summary>
  /// Whether <paramref name="perspectiveInterface"/> is one of the families whose <c>Apply</c> the generated runner calls
  /// (<c>IPerspectiveFor</c>, <c>IPerspectiveWithActionsFor</c>, <c>IGlobalPerspectiveFor</c>). An <c>Apply</c> that any
  /// other interface declares, including a custom interface that extends <c>IPerspectiveBase</c>, is never called by
  /// the runner: custom families are not supported yet (#1943).
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveMarkerOnlyEventTests.cs:AnEventListedOnlyByACustomInterfaceThatDeclaresItsOwnApply_IsAnErrorAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveMarkerOnlyEventTests.cs:AnEventAMarkerAndADeclaringInterfaceBothList_IsNotReportedAsync</tests>
  public static bool IsRunnerDispatchedFamily(INamedTypeSymbol perspectiveInterface) {
    var originalDef = TypeNameUtilities.Display(perspectiveInterface.OriginalDefinition);
    return originalDef.StartsWith(PERSPECTIVE_FOR + "<", StringComparison.Ordinal) ||
           originalDef.StartsWith(PERSPECTIVE_WITH_ACTIONS_FOR + "<", StringComparison.Ordinal) ||
           originalDef.StartsWith(GLOBAL_PERSPECTIVE_FOR + "<", StringComparison.Ordinal);
  }

  /// <summary>
  /// Checks if a class implements any perspective interface.
  /// </summary>
  public static bool IsPerspectiveClass(INamedTypeSymbol classSymbol) {
    return classSymbol.AllInterfaces.Any(i => {
      var originalDef = TypeNameUtilities.Display(i.OriginalDefinition);
      return originalDef.StartsWith(PERSPECTIVE_BASE + GENERIC_MODEL_EVENT_PREFIX, StringComparison.Ordinal) ||
             originalDef.StartsWith(PERSPECTIVE_FOR + GENERIC_MODEL_EVENT_PREFIX, StringComparison.Ordinal) ||
             originalDef.StartsWith(PERSPECTIVE_WITH_ACTIONS_FOR + GENERIC_MODEL_EVENT_PREFIX, StringComparison.Ordinal) ||
             originalDef.StartsWith(GLOBAL_PERSPECTIVE_FOR + "<TModel, TPartitionKey, TEvent", StringComparison.Ordinal);
      // Every definition matched above takes a model and at least one event, so a match has two or more
      // type arguments; the count needs no test of its own.
    });
  }



  /// <summary>
  /// Extracts all event types from perspective interfaces (skipping TModel at index 0).
  /// Deduplicates across multiple interfaces on the same class.
  /// </summary>
  public static List<ITypeSymbol> ExtractEventTypes(INamedTypeSymbol classSymbol) {
    var eventTypes = new List<ITypeSymbol>();
    var seen = new HashSet<string>();

    // S3267: Inner loop with index arithmetic — LINQ SelectMany would be less clear
#pragma warning disable S3267
    foreach (var iface in FindSingleStreamInterfaces(classSymbol)) {
      for (var i = 1; i < iface.TypeArguments.Length; i++) {
        var eventType = iface.TypeArguments[i];
        var key = TypeNameUtilities.FullyQualified(eventType);
        if (seen.Add(key)) {
          eventTypes.Add(eventType);
        }
      }
    }

    foreach (var iface in FindGlobalInterfaces(classSymbol)) {
      // Global: TypeArguments[0] = TModel, [1] = TPartitionKey, [2..N] = events
      for (var i = 2; i < iface.TypeArguments.Length; i++) {
        var eventType = iface.TypeArguments[i];
        var key = TypeNameUtilities.FullyQualified(eventType);
        if (seen.Add(key)) {
          eventTypes.Add(eventType);
        }
      }
    }
#pragma warning restore S3267

    return eventTypes;
  }
}
