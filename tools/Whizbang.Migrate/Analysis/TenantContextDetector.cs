using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Migrate.Analysis;

/// <summary>
/// Detects tenant context patterns in source code.
/// Scans for properties like TenantId, Scope, OrganizationId, and Marten tenant features.
/// </summary>
/// <docs>migration-guide/automated-migration#tenant-context-detection</docs>
public sealed class TenantContextDetector {
  private static readonly HashSet<string> _tenantPropertyPatterns = new(StringComparer.OrdinalIgnoreCase) {
    "TenantId",
    "Scope",
    "SecurityContext",
    "OrganizationId",
    "WorkspaceId",
    "CompanyId",
    "AccountId"
  };

  private static readonly Regex _forTenantPattern = new(
      @"\.ForTenant\s*\(",
      RegexOptions.Compiled);

  private static readonly Regex _openSessionWithTenantPattern = new(
      @"\.OpenSession\s*\(\s*""[^""]+""",
      RegexOptions.Compiled);

  /// <summary>
  /// Detects tenant context patterns in the given source code.
  /// </summary>
  /// <param name="sourceCode">C# source code to analyze.</param>
  /// <param name="filePath">File path for reporting.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>Detection result with tenant context information.</returns>
  public static Task<TenantContextDetectionResult> DetectAsync(
      string sourceCode,
      string filePath,
      CancellationToken ct = default) {
    var tree = CSharpSyntaxTree.ParseText(sourceCode, cancellationToken: ct);
    var root = tree.GetRoot(ct);

    var propertyOccurrences = new Dictionary<string, TenantPropertyInfo>(StringComparer.Ordinal);
    var martenPatterns = new List<string>();

    // Find all record declarations (events are typically records)
    var recordDeclarations = root.DescendantNodes()
        .OfType<RecordDeclarationSyntax>()
        .Where(_isLikelyEventRecord);

    foreach (var record in recordDeclarations) {
      _analyzeRecordParameters(record, propertyOccurrences);
    }

    // Check for Marten tenant patterns
    _detectMartenTenantPatterns(sourceCode, martenPatterns);

    var sortedProperties = propertyOccurrences.Values
        .OrderByDescending(p => p.OccurrenceCount)
        .ThenBy(p => p.PropertyName)
        .ToList();

    var result = new TenantContextDetectionResult {
      DetectedProperties = sortedProperties,
      MostCommon = sortedProperties.FirstOrDefault(),
      HasTenantContext = sortedProperties.Count > 0 || martenPatterns.Count > 0,
      UsesMartenTenantFeatures = martenPatterns.Count > 0,
      MartenTenantPatterns = martenPatterns
    };

    return Task.FromResult(result);
  }

  /// <summary>
  /// Detects tenant context from multiple source files.
  /// </summary>
  /// <param name="files">Dictionary of file path to source code.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>Aggregated detection result.</returns>
  public static async Task<TenantContextDetectionResult> DetectFromMultipleSourcesAsync(
      IReadOnlyDictionary<string, string> files,
      CancellationToken ct = default) {
    var aggregatedOccurrences = new Dictionary<string, TenantPropertyInfo>(StringComparer.Ordinal);
    var allMartenPatterns = new HashSet<string>(StringComparer.Ordinal);

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

      foreach (var pattern in fileResult.MartenTenantPatterns) {
        allMartenPatterns.Add(pattern);
      }
    }

    var sortedProperties = aggregatedOccurrences.Values
        .OrderByDescending(p => p.OccurrenceCount)
        .ThenBy(p => p.PropertyName)
        .ToList();

    return new TenantContextDetectionResult {
      DetectedProperties = sortedProperties,
      MostCommon = sortedProperties.FirstOrDefault(),
      HasTenantContext = sortedProperties.Count > 0 || allMartenPatterns.Count > 0,
      UsesMartenTenantFeatures = allMartenPatterns.Count > 0,
      MartenTenantPatterns = allMartenPatterns.ToList()
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
      Dictionary<string, TenantPropertyInfo> occurrences) {
    var parameters = record.ParameterList?.Parameters;
    if (parameters == null) {
      return;
    }

    foreach (var param in parameters) {
      var paramName = param.Identifier.Text;
      var typeName = param.Type?.ToString() ?? "";

      // Check if this is a potential tenant property
      if (!_isTenantProperty(paramName)) {
        continue;
      }

      var isTenantLike = _isTenantLikeProperty(paramName);

      if (occurrences.TryGetValue(paramName, out var existing)) {
        occurrences[paramName] = existing with {
          OccurrenceCount = existing.OccurrenceCount + 1
        };
      } else {
        occurrences[paramName] = new TenantPropertyInfo {
          PropertyName = paramName,
          TypeName = typeName,
          IsTenantLike = isTenantLike,
          OccurrenceCount = 1
        };
      }
    }
  }

  private static bool _isTenantProperty(string paramName) {
    return _tenantPropertyPatterns.Contains(paramName);
  }

  private static bool _isTenantLikeProperty(string paramName) {
    // These are properties that represent organizational/workspace boundaries
    return paramName.Equals("TenantId", StringComparison.OrdinalIgnoreCase) ||
           paramName.Equals("OrganizationId", StringComparison.OrdinalIgnoreCase) ||
           paramName.Equals("WorkspaceId", StringComparison.OrdinalIgnoreCase) ||
           paramName.Equals("CompanyId", StringComparison.OrdinalIgnoreCase) ||
           paramName.Equals("AccountId", StringComparison.OrdinalIgnoreCase);
  }

  private static void _detectMartenTenantPatterns(string sourceCode, List<string> patterns) {
    if (_forTenantPattern.IsMatch(sourceCode)) {
      patterns.Add("ForTenant");
    }

    if (_openSessionWithTenantPattern.IsMatch(sourceCode)) {
      patterns.Add("OpenSession with tenant");
    }
  }
}

/// <summary>
/// Result of tenant context detection.
/// </summary>
public sealed record TenantContextDetectionResult {
  /// <summary>
  /// All detected tenant-related properties, sorted by occurrence count (descending).
  /// </summary>
  public required IReadOnlyList<TenantPropertyInfo> DetectedProperties { get; init; }

  /// <summary>
  /// The most commonly occurring tenant property, or null if none found.
  /// </summary>
  public TenantPropertyInfo? MostCommon { get; init; }

  /// <summary>
  /// Whether any tenant context was detected (properties or Marten features).
  /// </summary>
  public required bool HasTenantContext { get; init; }

  /// <summary>
  /// Whether code uses Marten's built-in tenant features (ForTenant, tenant sessions).
  /// </summary>
  public required bool UsesMartenTenantFeatures { get; init; }

  /// <summary>
  /// List of detected Marten tenant patterns (e.g., "ForTenant", "OpenSession with tenant").
  /// </summary>
  public required IReadOnlyList<string> MartenTenantPatterns { get; init; }
}

/// <summary>
/// Information about a detected tenant property.
/// </summary>
public sealed record TenantPropertyInfo {
  /// <summary>
  /// Name of the property (e.g., "TenantId", "OrganizationId").
  /// </summary>
  public required string PropertyName { get; init; }

  /// <summary>
  /// Type name of the property (e.g., "string", "Guid").
  /// </summary>
  public required string TypeName { get; init; }

  /// <summary>
  /// Whether this property represents a tenant-like boundary (TenantId, OrganizationId, etc.).
  /// </summary>
  public required bool IsTenantLike { get; init; }

  /// <summary>
  /// Number of times this property appears across event records.
  /// </summary>
  public required int OccurrenceCount { get; init; }
}
