using Whizbang.Core.Security;

namespace Whizbang.Core.Lenses;

/// <summary>
/// Key-value extension for PerspectiveScope.
/// Used instead of Dictionary&lt;string,string?&gt; for EF Core ComplexProperty().ToJson() compatibility.
/// </summary>
/// <remarks>
/// EF Core does NOT support Dictionary with ToJson() (GitHub #29825).
/// Using a list of key-value objects enables full LINQ support via ComplexProperty().ToJson().
/// </remarks>
public class ScopeExtension {
  /// <summary>
  /// Parameterless constructor for JSON deserialization.
  /// </summary>
  public ScopeExtension() { }

  /// <summary>
  /// Creates a new scope extension with key and value.
  /// </summary>
  public ScopeExtension(string key, string? value) {
    Key = key;
    Value = value;
  }

  /// <summary>
  /// The extension key.
  /// </summary>
  public string Key { get; set; } = string.Empty;

  /// <summary>
  /// The extension value.
  /// </summary>
  public string? Value { get; set; }
}

/// <summary>
/// Multi-tenancy and security scope for perspective rows.
/// Stored as JSONB/JSON in scope column using EF Core ComplexProperty().ToJson().
/// </summary>
/// <docs>core-concepts/scoping#perspective-scope</docs>
/// <tests>Whizbang.Core.Tests/Scoping/PerspectiveScopeTests.cs</tests>
/// <example>
/// var scope = new PerspectiveScope {
///   TenantId = "tenant-123",
///   UserId = "user-456",
///   AllowedPrincipals = [
///     SecurityPrincipalId.Group("sales-team"),
///     SecurityPrincipalId.User("manager-789")
///   ]
/// };
///
/// // Access via GetValue method
/// var tenant = scope.GetValue("TenantId");      // "tenant-123"
/// var custom = scope.GetValue("CustomField");   // from Extensions
/// </example>
/// <remarks>
/// <para>
/// <strong>EF Core 10 ComplexProperty().ToJson() Support:</strong>
/// This type is designed for full LINQ query support via ComplexProperty().ToJson():
/// </para>
/// <list type="bullet">
/// <item>Extensions use <c>List&lt;ScopeExtension&gt;</c> (not Dictionary) for ToJson() compatibility</item>
/// <item>All properties support direct LINQ queries: <c>.Where(r =&gt; r.Scope.TenantId == "x")</c></item>
/// <item>Extension queries: <c>.Where(r =&gt; r.Scope.Extensions.Any(e =&gt; e.Key == "x"))</c></item>
/// </list>
/// <para>
/// Using a <c>class</c> (not <c>record</c>) allows EF Core ComplexProperty mapping.
/// </para>
/// </remarks>
public class PerspectiveScope {
  /// <summary>
  /// Parameterless constructor for JSON deserialization.
  /// </summary>
  public PerspectiveScope() { }

  /// <summary>
  /// The tenant identifier for multi-tenancy isolation.
  /// </summary>
  public string? TenantId { get; set; }

  /// <summary>
  /// The customer identifier for customer-level isolation.
  /// </summary>
  public string? CustomerId { get; set; }

  /// <summary>
  /// The user identifier for user-level isolation.
  /// </summary>
  public string? UserId { get; set; }

  /// <summary>
  /// The organization identifier for organization-level isolation.
  /// </summary>
  public string? OrganizationId { get; set; }

  /// <summary>
  /// Security principals (users, groups, services) that have access to this record.
  /// Stored as string values (e.g., "user:alice", "group:sales-team").
  /// Enables fine-grained access control: "who can see this record?"
  /// Query: WHERE AllowedPrincipals OVERLAPS caller.SecurityPrincipals
  /// </summary>
  /// <example>
  /// AllowedPrincipals = [
  ///   SecurityPrincipalId.Group("sales-team"),  // Implicitly converts to "group:sales-team"
  ///   SecurityPrincipalId.User("manager-456")   // Implicitly converts to "user:manager-456"
  /// ]
  /// </example>
  /// <remarks>
  /// Uses <c>List&lt;string&gt;</c> which serializes to JSON array.
  /// Principal filtering uses PostgreSQL's <c>@&gt;</c> (containment) and <c>?|</c> (array overlap)
  /// operators on the raw JSONB column for efficient GIN-indexed queries.
  /// <see cref="SecurityPrincipalId"/> has implicit conversion to/from string, so you can
  /// still use the factory methods when populating this list.
  /// </remarks>
  public List<string> AllowedPrincipals { get; set; } = [];

  /// <summary>
  /// Additional scope values as key-value pairs.
  /// Enables extensibility without schema changes.
  /// </summary>
  /// <remarks>
  /// Uses <c>List&lt;ScopeExtension&gt;</c> for EF Core ComplexProperty().ToJson() compatibility.
  /// Dictionary is NOT supported with ToJson() (GitHub #29825).
  /// Query extensions with LINQ: <c>.Where(r =&gt; r.Scope.Extensions.Any(e =&gt; e.Key == "region"))</c>
  /// </remarks>
  public List<ScopeExtension> Extensions { get; set; } = [];

  /// <summary>
  /// Gets a scope value by key (searches standard properties then Extensions).
  /// </summary>
  /// <param name="key">The property name to access.</param>
  /// <returns>The value of the property, or null if not found.</returns>
  /// <remarks>
  /// Implemented as a method instead of indexer for EF Core ComplexProperty compatibility.
  /// Indexers are discovered as "Item" properties by EF Core, causing mapping issues.
  /// </remarks>
  public string? GetValue(string key) => key switch {
    nameof(TenantId) => TenantId,
    nameof(CustomerId) => CustomerId,
    nameof(UserId) => UserId,
    nameof(OrganizationId) => OrganizationId,
    _ => Extensions.FirstOrDefault(e => e.Key == key)?.Value
  };

  /// <summary>
  /// Sets an extension value by key. Creates or updates the extension.
  /// </summary>
  /// <param name="key">The extension key.</param>
  /// <param name="value">The extension value.</param>
  public void SetExtension(string key, string? value) {
    var existing = Extensions.FirstOrDefault(e => e.Key == key);
    if (existing is not null) {
      existing.Value = value;
    } else {
      Extensions.Add(new ScopeExtension(key, value));
    }
  }

  /// <summary>
  /// Removes an extension by key.
  /// </summary>
  /// <param name="key">The extension key to remove.</param>
  /// <returns>True if the extension was found and removed.</returns>
  public bool RemoveExtension(string key) {
    var existing = Extensions.FirstOrDefault(e => e.Key == key);
    return existing is not null && Extensions.Remove(existing);
  }
}
