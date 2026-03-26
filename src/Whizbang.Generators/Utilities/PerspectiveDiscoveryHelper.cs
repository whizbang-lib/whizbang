using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

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

  /// <summary>
  /// Finds all single-stream perspective interfaces on a class.
  /// Scans IPerspectiveBase, IPerspectiveFor, and IPerspectiveWithActionsFor.
  /// Returns interfaces with TModel as first type arg and TEvent1..N as remaining args.
  /// </summary>
  public static List<INamedTypeSymbol> FindSingleStreamInterfaces(INamedTypeSymbol classSymbol) {
    return [.. classSymbol.AllInterfaces
      .Where(i => {
        var originalDef = i.OriginalDefinition.ToDisplayString();
        // Match with "<TModel, TEvent" prefix to skip marker-only bases like IPerspectiveBase<TModel>
        return (originalDef.StartsWith(PERSPECTIVE_BASE + "<TModel, TEvent", StringComparison.Ordinal) ||
                originalDef.StartsWith(PERSPECTIVE_FOR + "<TModel, TEvent", StringComparison.Ordinal) ||
                originalDef.StartsWith(PERSPECTIVE_WITH_ACTIONS_FOR + "<TModel, TEvent", StringComparison.Ordinal))
               && i.TypeArguments.Length >= 2;
      })];
  }

  /// <summary>
  /// Finds all global (multi-stream) perspective interfaces on a class.
  /// </summary>
  public static List<INamedTypeSymbol> FindGlobalInterfaces(INamedTypeSymbol classSymbol) {
    return [.. classSymbol.AllInterfaces
      .Where(i => {
        var originalDef = i.OriginalDefinition.ToDisplayString();
        return originalDef.StartsWith(GLOBAL_PERSPECTIVE_FOR + "<", StringComparison.Ordinal)
               && i.TypeArguments.Length >= 3;
      })];
  }

  /// <summary>
  /// Finds the first matching perspective interface (single-stream or global).
  /// Returns null if no perspective interface is found.
  /// </summary>
  public static INamedTypeSymbol? FindFirstPerspectiveInterface(INamedTypeSymbol classSymbol) {
    return FindSingleStreamInterfaces(classSymbol).FirstOrDefault()
      ?? FindGlobalInterfaces(classSymbol).FirstOrDefault();
  }

  /// <summary>
  /// Checks if a class implements any perspective interface.
  /// </summary>
  public static bool IsPerspectiveClass(INamedTypeSymbol classSymbol) {
    return classSymbol.AllInterfaces.Any(i => {
      var originalDef = i.OriginalDefinition.ToDisplayString();
      return (originalDef.StartsWith(PERSPECTIVE_BASE + "<TModel, TEvent", StringComparison.Ordinal) ||
              originalDef.StartsWith(PERSPECTIVE_FOR + "<TModel, TEvent", StringComparison.Ordinal) ||
              originalDef.StartsWith(PERSPECTIVE_WITH_ACTIONS_FOR + "<TModel, TEvent", StringComparison.Ordinal) ||
              originalDef.StartsWith(GLOBAL_PERSPECTIVE_FOR + "<TModel, TPartitionKey, TEvent", StringComparison.Ordinal))
             && i.TypeArguments.Length >= 2;
    });
  }

  /// <summary>
  /// Checks if a class implements IPerspectiveWithActionsFor for a specific event type.
  /// Used by code gen to determine if Apply returns ApplyResult vs TModel.
  /// </summary>
  public static bool HasWithActionsForEvent(INamedTypeSymbol classSymbol, ITypeSymbol eventType) {
    return classSymbol.AllInterfaces.Any(i => {
      var originalDef = i.OriginalDefinition.ToDisplayString();
      return originalDef.StartsWith(PERSPECTIVE_WITH_ACTIONS_FOR + "<", StringComparison.Ordinal)
             && i.TypeArguments.Length >= 2
             && SymbolEqualityComparer.Default.Equals(i.TypeArguments[1], eventType);
    });
  }

  /// <summary>
  /// Extracts the model type from the first type argument of any perspective interface.
  /// </summary>
  public static ITypeSymbol? ExtractModelType(INamedTypeSymbol classSymbol) {
    var iface = FindFirstPerspectiveInterface(classSymbol);
    return iface?.TypeArguments[0];
  }

  /// <summary>
  /// Extracts all event types from perspective interfaces (skipping TModel at index 0).
  /// Deduplicates across multiple interfaces on the same class.
  /// </summary>
  public static List<ITypeSymbol> ExtractEventTypes(INamedTypeSymbol classSymbol) {
    var eventTypes = new List<ITypeSymbol>();
    var seen = new HashSet<string>();

    foreach (var iface in FindSingleStreamInterfaces(classSymbol)) {
      for (var i = 1; i < iface.TypeArguments.Length; i++) {
        var eventType = iface.TypeArguments[i];
        var key = eventType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (seen.Add(key)) {
          eventTypes.Add(eventType);
        }
      }
    }

    foreach (var iface in FindGlobalInterfaces(classSymbol)) {
      // Global: TypeArguments[0] = TModel, [1] = TPartitionKey, [2..N] = events
      for (var i = 2; i < iface.TypeArguments.Length; i++) {
        var eventType = iface.TypeArguments[i];
        var key = eventType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (seen.Add(key)) {
          eventTypes.Add(eventType);
        }
      }
    }

    return eventTypes;
  }
}
