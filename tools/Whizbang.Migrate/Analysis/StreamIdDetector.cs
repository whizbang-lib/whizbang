using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Migrate.Analysis;

/// <summary>
/// Detects potential stream ID properties in event records.
/// Scans for common patterns like StreamId, AggregateId, Id, and domain-specific *Id properties.
/// </summary>
/// <docs>migration-guide/automated-migration#stream-id-detection</docs>
public sealed class StreamIdDetector {
  private static readonly HashSet<string> _guidTypeNames = new(StringComparer.Ordinal) {
    "Guid",
    "System.Guid"
  };

  private static readonly HashSet<string> _commonIdPatterns = new(StringComparer.OrdinalIgnoreCase) {
    "StreamId",
    "AggregateId",
    "Id"
  };

  /// <summary>
  /// Detects potential stream ID properties in the given source code.
  /// </summary>
  /// <param name="sourceCode">C# source code to analyze.</param>
  /// <param name="filePath">File path for reporting.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>Detection result with ranked properties.</returns>
  public static Task<StreamIdDetectionResult> DetectAsync(
      string sourceCode,
      string filePath,
      CancellationToken ct = default) {
    var tree = CSharpSyntaxTree.ParseText(sourceCode, cancellationToken: ct);
    var root = tree.GetRoot(ct);

    var propertyOccurrences = new Dictionary<string, StreamIdPropertyInfo>(StringComparer.Ordinal);

    // Find all record declarations (events are typically records)
    var recordDeclarations = root.DescendantNodes()
        .OfType<RecordDeclarationSyntax>()
        .Where(_isLikelyEventRecord);

    foreach (var record in recordDeclarations) {
      _analyzeRecordParameters(record, propertyOccurrences);
    }

    var sortedProperties = propertyOccurrences.Values
        .OrderByDescending(p => p.OccurrenceCount)
        .ThenBy(p => p.PropertyName)
        .ToList();

    var result = new StreamIdDetectionResult {
      DetectedProperties = sortedProperties,
      MostCommon = sortedProperties.FirstOrDefault(),
      HasDetections = sortedProperties.Count > 0
    };

    return Task.FromResult(result);
  }

  /// <summary>
  /// Detects stream ID properties from multiple source files.
  /// </summary>
  /// <param name="files">Dictionary of file path to source code.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>Aggregated detection result.</returns>
  public static async Task<StreamIdDetectionResult> DetectFromMultipleSourcesAsync(
      IReadOnlyDictionary<string, string> files,
      CancellationToken ct = default) {
    var aggregatedOccurrences = new Dictionary<string, StreamIdPropertyInfo>(StringComparer.Ordinal);

    foreach (var (filePath, sourceCode) in files) {
      ct.ThrowIfCancellationRequested();

      var fileResult = await DetectAsync(sourceCode, filePath, ct);

      foreach (var prop in fileResult.DetectedProperties) {
        if (aggregatedOccurrences.TryGetValue(prop.PropertyName, out var existing)) {
          aggregatedOccurrences[prop.PropertyName] = existing with {
            OccurrenceCount = existing.OccurrenceCount + prop.OccurrenceCount
          };
        } else {
          aggregatedOccurrences[prop.PropertyName] = prop;
        }
      }
    }

    var sortedProperties = aggregatedOccurrences.Values
        .OrderByDescending(p => p.OccurrenceCount)
        .ThenBy(p => p.PropertyName)
        .ToList();

    return new StreamIdDetectionResult {
      DetectedProperties = sortedProperties,
      MostCommon = sortedProperties.FirstOrDefault(),
      HasDetections = sortedProperties.Count > 0
    };
  }

  private static bool _isLikelyEventRecord(RecordDeclarationSyntax record) {
    var name = record.Identifier.Text;
    // Common event naming patterns
    return name.EndsWith("Event", StringComparison.Ordinal) ||
           name.EndsWith("Created", StringComparison.Ordinal) ||
           name.EndsWith("Updated", StringComparison.Ordinal) ||
           name.EndsWith("Deleted", StringComparison.Ordinal) ||
           name.EndsWith("Changed", StringComparison.Ordinal) ||
           name.EndsWith("Cancelled", StringComparison.Ordinal) ||
           name.EndsWith("Completed", StringComparison.Ordinal) ||
           name.EndsWith("Started", StringComparison.Ordinal) ||
           name.EndsWith("Shipped", StringComparison.Ordinal) ||
           name.EndsWith("Delivered", StringComparison.Ordinal) ||
           // If it's a positional record (parameter list), it's likely an event
           record.ParameterList != null;
  }

  private static void _analyzeRecordParameters(
      RecordDeclarationSyntax record,
      Dictionary<string, StreamIdPropertyInfo> occurrences) {
    var parameters = record.ParameterList?.Parameters;
    if (parameters == null) {
      return;
    }

    foreach (var param in parameters) {
      var paramName = param.Identifier.Text;
      var typeName = param.Type?.ToString() ?? "";

      // Check if this is a potential ID property
      if (!_isIdProperty(paramName)) {
        continue;
      }

      // Check if the type is Guid or strongly-typed ID
      var isGuid = _isGuidType(typeName);
      var isStronglyTyped = _isStronglyTypedId(typeName, paramName);

      if (!isGuid && !isStronglyTyped) {
        continue;
      }

      if (occurrences.TryGetValue(paramName, out var existing)) {
        occurrences[paramName] = existing with {
          OccurrenceCount = existing.OccurrenceCount + 1
        };
      } else {
        occurrences[paramName] = new StreamIdPropertyInfo {
          PropertyName = paramName,
          TypeName = typeName,
          IsStronglyTyped = isStronglyTyped,
          OccurrenceCount = 1
        };
      }
    }
  }

  private static bool _isIdProperty(string paramName) {
    // Exact matches for common patterns
    if (_commonIdPatterns.Contains(paramName)) {
      return true;
    }

    // Domain-specific *Id pattern (e.g., OrderId, CustomerId)
    return paramName.EndsWith("Id", StringComparison.Ordinal) && paramName.Length > 2;
  }

  private static bool _isGuidType(string typeName) {
    return _guidTypeNames.Contains(typeName);
  }

  private static bool _isStronglyTypedId(string typeName, string paramName) {
    // A strongly-typed ID typically has the same name as the parameter
    // e.g., OrderId type for OrderId parameter
    return typeName.Equals(paramName, StringComparison.Ordinal);
  }
}

/// <summary>
/// Result of stream ID property detection.
/// </summary>
public sealed record StreamIdDetectionResult {
  /// <summary>
  /// All detected ID properties, sorted by occurrence count (descending).
  /// </summary>
  public required IReadOnlyList<StreamIdPropertyInfo> DetectedProperties { get; init; }

  /// <summary>
  /// The most commonly occurring ID property, or null if none found.
  /// </summary>
  public StreamIdPropertyInfo? MostCommon { get; init; }

  /// <summary>
  /// Whether any ID properties were detected.
  /// </summary>
  public required bool HasDetections { get; init; }
}

/// <summary>
/// Information about a detected stream ID property.
/// </summary>
public sealed record StreamIdPropertyInfo {
  /// <summary>
  /// Name of the property (e.g., "OrderId", "StreamId").
  /// </summary>
  public required string PropertyName { get; init; }

  /// <summary>
  /// Type name of the property (e.g., "Guid", "OrderId").
  /// </summary>
  public required string TypeName { get; init; }

  /// <summary>
  /// Whether this is a strongly-typed ID (type name matches property name).
  /// </summary>
  public required bool IsStronglyTyped { get; init; }

  /// <summary>
  /// Number of times this property appears across event records.
  /// </summary>
  public required int OccurrenceCount { get; init; }
}
