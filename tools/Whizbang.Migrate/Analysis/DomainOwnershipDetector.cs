using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Migrate.Analysis;

/// <summary>
/// Detects domain ownership patterns from source code by analyzing namespaces and type names.
/// Extracts domain names from hierarchical namespaces (MyApp.Orders.Events → "orders")
/// and flat namespaces (MyApp.Contracts.Commands.CreateOrder → "order" from type name).
/// </summary>
/// <docs>migration-guide/automated-migration#domain-ownership-detection</docs>
public static class DomainOwnershipDetector {
  private static readonly HashSet<string> _genericSegments = new(StringComparer.OrdinalIgnoreCase) {
    "contracts",
    "commands",
    "events",
    "queries",
    "messages",
    "handlers",
    "services",
    "infrastructure",
    "core",
    "common",
    "shared"
  };

  private static readonly string[] _typeSuffixes = [
    "Command",
    "Event",
    "Query",
    "Message",
    "Handler",
    "Receptor",
    "Created",
    "Updated",
    "Deleted",
    "Changed",
    "Cancelled",
    "Completed",
    "Started",
    "Shipped",
    "Delivered",
    "Processed",
    "Placed",
    "Refunded",
    "Reserved"
  ];

  private static readonly string[] _typePrefixes = [
    "Create",
    "Update",
    "Delete",
    "Get",
    "Set",
    "Process",
    "Place",
    "Cancel"
  ];

  /// <summary>
  /// Detects domain ownership patterns from the given source code.
  /// </summary>
  /// <param name="sourceCode">C# source code to analyze.</param>
  /// <param name="filePath">File path for reporting.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>Detection result with ranked domains.</returns>
  public static Task<DomainDetectionResult> DetectAsync(
      string sourceCode,
      string filePath,
      CancellationToken ct = default) {
    if (string.IsNullOrWhiteSpace(sourceCode)) {
      return Task.FromResult(new DomainDetectionResult {
        DetectedDomains = [],
        MostCommon = null,
        HasDetections = false
      });
    }

    var tree = CSharpSyntaxTree.ParseText(sourceCode, cancellationToken: ct);
    var root = tree.GetRoot(ct);

    var domainOccurrences = new Dictionary<string, DomainInfo>(StringComparer.OrdinalIgnoreCase);

    // Get the namespace name (handles both file-scoped and block-scoped)
    var namespaceDeclaration = root.DescendantNodes()
        .OfType<BaseNamespaceDeclarationSyntax>()
        .FirstOrDefault();

    var namespaceName = namespaceDeclaration?.Name.ToString() ?? "";
    var domainFromNamespace = _extractDomainFromNamespace(namespaceName);

    // Find all message types in the file
    var typeDeclarations = root.DescendantNodes()
        .OfType<TypeDeclarationSyntax>()
        .Where(_isMessageType);

    foreach (var type in typeDeclarations) {
      ct.ThrowIfCancellationRequested();

      var typeName = type.Identifier.Text;
      string domain;
      bool fromNamespace;
      bool fromTypeName;

      if (domainFromNamespace is not null) {
        // Use namespace-derived domain
        domain = domainFromNamespace;
        fromNamespace = true;
        fromTypeName = false;
      } else {
        // Extract from type name
        domain = _extractDomainFromTypeName(typeName);
        fromNamespace = false;
        fromTypeName = true;
      }

      if (string.IsNullOrEmpty(domain)) {
        continue;
      }

      if (domainOccurrences.TryGetValue(domain, out var existing)) {
        domainOccurrences[domain] = existing with {
          OccurrenceCount = existing.OccurrenceCount + 1
        };
      } else {
        domainOccurrences[domain] = new DomainInfo {
          DomainName = domain.ToLowerInvariant(),
          OccurrenceCount = 1,
          FromNamespace = fromNamespace,
          FromTypeName = fromTypeName
        };
      }
    }

    var sortedDomains = domainOccurrences.Values
        .OrderByDescending(d => d.OccurrenceCount)
        .ThenBy(d => d.DomainName)
        .ToList();

    return Task.FromResult(new DomainDetectionResult {
      DetectedDomains = sortedDomains,
      MostCommon = sortedDomains.FirstOrDefault(),
      HasDetections = sortedDomains.Count > 0
    });
  }

  /// <summary>
  /// Detects domain patterns from multiple source files.
  /// </summary>
  /// <param name="files">Dictionary of file path to source code.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>Aggregated detection result.</returns>
  public static async Task<DomainDetectionResult> DetectFromMultipleSourcesAsync(
      IReadOnlyDictionary<string, string> files,
      CancellationToken ct = default) {
    var aggregatedOccurrences = new Dictionary<string, DomainInfo>(StringComparer.OrdinalIgnoreCase);

    foreach (var (filePath, sourceCode) in files) {
      ct.ThrowIfCancellationRequested();

      var fileResult = await DetectAsync(sourceCode, filePath, ct);

      foreach (var domain in fileResult.DetectedDomains) {
        if (aggregatedOccurrences.TryGetValue(domain.DomainName, out var existing)) {
          aggregatedOccurrences[domain.DomainName] = existing with {
            OccurrenceCount = existing.OccurrenceCount + domain.OccurrenceCount,
            FromNamespace = existing.FromNamespace || domain.FromNamespace,
            FromTypeName = existing.FromTypeName || domain.FromTypeName
          };
        } else {
          aggregatedOccurrences[domain.DomainName] = domain;
        }
      }
    }

    var sortedDomains = aggregatedOccurrences.Values
        .OrderByDescending(d => d.OccurrenceCount)
        .ThenBy(d => d.DomainName)
        .ToList();

    return new DomainDetectionResult {
      DetectedDomains = sortedDomains,
      MostCommon = sortedDomains.FirstOrDefault(),
      HasDetections = sortedDomains.Count > 0
    };
  }

  private static bool _isMessageType(TypeDeclarationSyntax type) {
    // Records are typical for events/commands
    if (type is RecordDeclarationSyntax) {
      return true;
    }

    // Also check for classes with message-like suffixes
    var name = type.Identifier.Text;
    return _typeSuffixes.Any(suffix =>
        name.EndsWith(suffix, StringComparison.Ordinal));
  }

  private static string? _extractDomainFromNamespace(string namespaceName) {
    var parts = namespaceName.Split('.');

    // Look for a non-generic segment that could be a domain
    // Traverse from end, skipping generic segments
    for (var i = parts.Length - 1; i >= 0; i--) {
      var segment = parts[i];
      if (!_genericSegments.Contains(segment)) {
        // Found a potential domain segment
        // But don't use the root namespace (typically company name)
        if (i > 0) {
          return segment.ToLowerInvariant();
        }
      }
    }

    // All segments are generic - need to extract from type names
    return null;
  }

  private static string _extractDomainFromTypeName(string typeName) {
    var name = typeName;

    // Remove suffixes repeatedly (handles "OrderPlacedEvent" → "OrderPlaced" → "Order")
    var changed = true;
    while (changed) {
      changed = false;
      foreach (var suffix in _typeSuffixes) {
        if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length) {
          name = name[..^suffix.Length];
          changed = true;
          break;
        }
      }
    }

    // Remove prefixes
    foreach (var prefix in _typePrefixes) {
      if (name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length) {
        name = name[prefix.Length..];
        break;
      }
    }

    return name.ToLowerInvariant();
  }
}

/// <summary>
/// Result of domain ownership detection.
/// </summary>
public sealed record DomainDetectionResult {
  /// <summary>
  /// All detected domains, sorted by occurrence count (descending).
  /// </summary>
  public required IReadOnlyList<DomainInfo> DetectedDomains { get; init; }

  /// <summary>
  /// The most commonly occurring domain, or null if none found.
  /// </summary>
  public DomainInfo? MostCommon { get; init; }

  /// <summary>
  /// Whether any domains were detected.
  /// </summary>
  public required bool HasDetections { get; init; }
}

/// <summary>
/// Information about a detected domain.
/// </summary>
public sealed record DomainInfo {
  /// <summary>
  /// Name of the domain (lowercase, e.g., "orders", "inventory").
  /// </summary>
  public required string DomainName { get; init; }

  /// <summary>
  /// Number of times this domain appears across message types.
  /// </summary>
  public required int OccurrenceCount { get; init; }

  /// <summary>
  /// Whether this domain was extracted from a namespace segment.
  /// </summary>
  public required bool FromNamespace { get; init; }

  /// <summary>
  /// Whether this domain was extracted from a type name.
  /// </summary>
  public required bool FromTypeName { get; init; }
}
