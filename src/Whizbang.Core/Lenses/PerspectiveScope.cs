using System.Text.Json.Serialization;
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
  [JsonPropertyName("k")]
  public string Key { get; set; } = string.Empty;

  /// <summary>
  /// The extension value.
  /// </summary>
  [JsonPropertyName("v")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public string? Value { get; set; }
}

/// <summary>
/// Multi-tenancy and security scope for perspective rows.
/// Stored as JSONB/JSON in scope column using EF Core ComplexProperty().ToJson().
/// </summary>
/// <docs>fundamentals/security/scoping#perspective-scope</docs>
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
  [JsonPropertyName("t")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public string? TenantId { get; set; }

  /// <summary>
  /// The customer identifier for customer-level isolation.
  /// </summary>
  [JsonPropertyName("c")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public string? CustomerId { get; set; }

  /// <summary>
  /// The user identifier for user-level isolation.
  /// </summary>
  [JsonPropertyName("u")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public string? UserId { get; set; }

  /// <summary>
  /// The organization identifier for organization-level isolation.
  /// </summary>
  [JsonPropertyName("o")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
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
  [JsonPropertyName("ap")]
  public List<string> AllowedPrincipals { get; set; } = [];

  /// <summary>
  /// Marks a scope as SYSTEM-originated: published by the framework or a background worker with no
  /// ambient user, by design.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This states INTENT, never permission. A system scope grants no tenant, no user and no
  /// principal — treating it as authority would turn a diagnostic marker into privilege escalation.
  /// </para>
  /// <para>
  /// It exists so that an ABSENT scope means exactly one thing. Control-plane traffic legitimately
  /// carries no user, and previously stored a null scope — identical in storage to a business event
  /// that had lost its scope. With the two indistinguishable, "scope is null" could not be asserted
  /// as a fault, and an audit of stored scope reported healthy data while a large population of
  /// events had silently lost theirs.
  /// </para>
  /// </remarks>
  /// <docs>fundamentals/security/message-security#scope-markers</docs>
  [JsonPropertyName("sys")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public bool IsSystem { get; set; }

  /// <summary>
  /// Marks a scope the APPLICATION AUTHOR declared absent: a pre-authentication event, a health
  /// check, an anonymous or public action.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Distinct from <see cref="IsSystem"/> on purpose. That one means framework infrastructure and
  /// is stamped by the framework; this one records that a human asserted the event needs no scope.
  /// The two warrant different scrutiny in a security review, and if application code could claim
  /// the system marker it would become a blanket way to silence the missing-scope invariant.
  /// </para>
  /// <para>
  /// Like the system marker it states intent, never permission: it resolves to no tenant and no
  /// user, which matters most here — a login attempt is exactly where a fabricated authority would
  /// do the most damage.
  /// </para>
  /// </remarks>
  /// <docs>fundamentals/security/message-security#declaring-unscoped</docs>
  [JsonPropertyName("dec")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public bool IsDeclaredUnscoped { get; set; }

  /// <summary>
  /// Additional scope values as key-value pairs.
  /// Enables extensibility without schema changes.
  /// </summary>
  /// <remarks>
  /// Uses <c>List&lt;ScopeExtension&gt;</c> for EF Core ComplexProperty().ToJson() compatibility.
  /// Dictionary is NOT supported with ToJson() (GitHub #29825).
  /// Query extensions with LINQ: <c>.Where(r =&gt; r.Scope.Extensions.Any(e =&gt; e.Key == "region"))</c>
  /// </remarks>
  [JsonPropertyName("ex")]
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

  /// <summary>
  /// Returns a NEW <see cref="PerspectiveScope"/> containing only the fields named by
  /// <paramref name="fields"/>. Fields not in the set are left at their type defaults
  /// (null for the four ID strings, empty list for AllowedPrincipals and Extensions).
  /// Used by the perspective projection runner to honor <see cref="InheritScopeAttribute"/>
  /// — the runner takes the envelope's full scope and filters it through the perspective
  /// model's declared inheritance flags before persisting.
  /// </summary>
  /// <param name="fields">Bitwise combination of fields to retain.</param>
  /// <returns>A new scope containing only the requested fields.</returns>
  /// <remarks>
  /// Returns an empty scope (all defaults) when <paramref name="fields"/> is
  /// <see cref="ScopeFields.None"/>. Lists are shallow-copied — mutating the returned
  /// scope's lists does not affect the source.
  /// </remarks>
  public PerspectiveScope FilterByFields(ScopeFields fields) {
    return new PerspectiveScope {
      TenantId = (fields & ScopeFields.Tenant) != 0 ? TenantId : null,
      CustomerId = (fields & ScopeFields.Customer) != 0 ? CustomerId : null,
      UserId = (fields & ScopeFields.User) != 0 ? UserId : null,
      OrganizationId = (fields & ScopeFields.Organization) != 0 ? OrganizationId : null,
      AllowedPrincipals = (fields & ScopeFields.AllowedPrincipals) != 0
        ? [.. AllowedPrincipals]
        : [],
      Extensions = (fields & ScopeFields.Extensions) != 0
        ? [.. Extensions]
        : [],
    };
  }

  /// <summary>
  /// Returns a NEW <see cref="PerspectiveScope"/> that merges <paramref name="other"/>
  /// into this scope field-by-field: non-null/non-empty fields on <paramref name="other"/>
  /// overwrite this instance's corresponding field; null/empty fields on
  /// <paramref name="other"/> preserve this instance's value.
  /// <see cref="AllowedPrincipals"/> and <see cref="Extensions"/> are concatenated and
  /// deduplicated (extensions by <see cref="ScopeExtension.Key"/>).
  /// </summary>
  /// <remarks>
  /// Used by <see cref="UpdateStreamScopeCommand"/> with <c>ScopeMutationMode.Merge</c>.
  /// Neither input is mutated.
  /// </remarks>
  public PerspectiveScope MergeWith(PerspectiveScope other) {
    var mergedExtensions = new List<ScopeExtension>(Extensions);
    foreach (var ext in other.Extensions) {
      var existing = mergedExtensions.FirstOrDefault(e => e.Key == ext.Key);
      if (existing is not null) {
        existing.Value = ext.Value;
      } else {
        mergedExtensions.Add(new ScopeExtension(ext.Key, ext.Value));
      }
    }

    var mergedPrincipals = new List<string>(AllowedPrincipals);
    foreach (var p in other.AllowedPrincipals) {
      if (!mergedPrincipals.Contains(p)) {
        mergedPrincipals.Add(p);
      }
    }

    return new PerspectiveScope {
      TenantId = !string.IsNullOrEmpty(other.TenantId) ? other.TenantId : TenantId,
      UserId = !string.IsNullOrEmpty(other.UserId) ? other.UserId : UserId,
      CustomerId = !string.IsNullOrEmpty(other.CustomerId) ? other.CustomerId : CustomerId,
      OrganizationId = !string.IsNullOrEmpty(other.OrganizationId) ? other.OrganizationId : OrganizationId,
      AllowedPrincipals = mergedPrincipals,
      Extensions = mergedExtensions,
    };
  }
}
