using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Security;

/// <summary>
/// Identifies scope properties for delta storage on message hops.
/// Uses byte-sized enum for minimal serialization.
/// </summary>
/// <docs>fundamentals/security/scope-propagation</docs>
/// <tests>Whizbang.Core.Tests/Security/ScopeDeltaTests.cs</tests>
/// <remarks>
/// Serializes with 2-character abbreviated names via <see cref="ScopePropJsonConverter"/>:
/// Scope=Sc, Roles=Ro, Perms=Pe, Principals=Pr, Claims=Cl, Actual=Ac, Effective=Ef, Type=Ty
/// </remarks>
[JsonConverter(typeof(ScopePropJsonConverter))]
public enum ScopeProp : byte {
  /// <summary>PerspectiveScope values (TenantId, UserId, etc.).</summary>
  Scope = 0,

  /// <summary>Role names assigned to caller.</summary>
  Roles = 1,

  /// <summary>Permissions from roles + direct grants.</summary>
  Perms = 2,

  /// <summary>Security principal IDs the caller belongs to.</summary>
  Principals = 3,

  /// <summary>Raw claims from authentication.</summary>
  Claims = 4,

  /// <summary>Actual principal who initiated operation.</summary>
  Actual = 5,

  /// <summary>Effective principal the operation runs as.</summary>
  Effective = 6,

  /// <summary>Security context type (User, System, Impersonated, ServiceAccount).</summary>
  Type = 7
}

/// <summary>
/// Changes to a collection (roles, permissions, principals, claims).
/// Supports Set (replace all), Add, and Remove operations.
/// </summary>
/// <docs>fundamentals/security/scope-propagation</docs>
/// <tests>Whizbang.Core.Tests/Security/ScopeDeltaTests.cs</tests>
/// <remarks>
/// Apply order: Set takes precedence, otherwise Remove first then Add.
/// Missing property = inherit from previous hop.
/// </remarks>
public readonly struct CollectionChanges {
  /// <summary>
  /// Replace entire collection with these values. If present, Add/Remove are ignored.
  /// </summary>
  [JsonPropertyName("s")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public JsonElement? Set { get; init; }

  /// <summary>
  /// Add these values to the collection.
  /// </summary>
  [JsonPropertyName("a")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public JsonElement? Add { get; init; }

  /// <summary>
  /// Remove these values from the collection.
  /// </summary>
  [JsonPropertyName("r")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public JsonElement? Remove { get; init; }

  /// <summary>
  /// True if any operation is defined.
  /// </summary>
  [JsonIgnore]
  public bool HasChanges => Set.HasValue || Add.HasValue || Remove.HasValue;
}

/// <summary>
/// Delta-based scope changes for message hops.
/// Only stores what changed from previous hop to minimize wire size.
/// </summary>
/// <docs>fundamentals/security/scope-propagation</docs>
/// <tests>Whizbang.Core.Tests/Security/ScopeDeltaTests.cs</tests>
/// <remarks>
/// <para>
/// Used instead of storing full scope on every hop. Pattern:
/// </para>
/// <list type="bullet">
/// <item>First hop: full scope as delta from null</item>
/// <item>Subsequent hops: only what changed (or null if nothing changed)</item>
/// <item>GetCurrentScope(): walks hops, merges deltas to rebuild full ScopeContext</item>
/// </list>
/// </remarks>
public sealed class ScopeDelta {
  /// <summary>
  /// Simple value changes (Scope, Actual, Effective, Type).
  /// Key is ScopeProp enum, value is JsonElement containing the new value.
  /// </summary>
  [JsonPropertyName("v")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public Dictionary<ScopeProp, JsonElement>? Values { get; init; }

  /// <summary>
  /// Collection changes (Roles, Perms, Principals, Claims).
  /// Key is ScopeProp enum, value is CollectionChanges with Set/Add/Remove operations.
  /// </summary>
  [JsonPropertyName("c")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public Dictionary<ScopeProp, CollectionChanges>? Collections { get; init; }

  /// <summary>
  /// True if any values or collections have changes.
  /// </summary>
  [JsonIgnore]
  public bool HasChanges => (Values?.Count > 0) || (Collections?.Count > 0);

  /// <summary>
  /// Creates a ScopeDelta from a PerspectiveScope.
  /// Used when restoring scope from the database scope column into envelope hops.
  /// </summary>
  /// <param name="scope">The PerspectiveScope to create a delta from.</param>
  /// <returns>A ScopeDelta containing the scope, or null if the scope is null or entirely empty.</returns>
  public static ScopeDelta? FromPerspectiveScope(PerspectiveScope? scope) {
    if (scope == null) {
      return null;
    }

    if (string.IsNullOrEmpty(scope.TenantId) && string.IsNullOrEmpty(scope.UserId)
        && string.IsNullOrEmpty(scope.CustomerId) && string.IsNullOrEmpty(scope.OrganizationId)) {
      return null;
    }

    return new ScopeDelta {
      Values = new Dictionary<ScopeProp, JsonElement> {
        [ScopeProp.Scope] = _serializeScope(scope)
      }
    };
  }

  /// <summary>
  /// Creates a ScopeDelta from the old SecurityContext type.
  /// This is a backward compatibility helper for migrating from SecurityContext to ScopeDelta.
  /// </summary>
  /// <param name="securityContext">The old SecurityContext (with TenantId/UserId).</param>
  /// <returns>A ScopeDelta containing the scope, or null if the security context is null or empty.</returns>
  public static ScopeDelta? FromSecurityContext(SecurityContext? securityContext) {
    if (securityContext == null) {
      return null;
    }

    // If both are null/empty, return null (no changes)
    if (string.IsNullOrEmpty(securityContext.TenantId) && string.IsNullOrEmpty(securityContext.UserId)) {
      return null;
    }

    // Create a delta with just the Scope value set
    var scope = new PerspectiveScope {
      TenantId = securityContext.TenantId,
      UserId = securityContext.UserId
    };

    return new ScopeDelta {
      Values = new Dictionary<ScopeProp, JsonElement> {
        [ScopeProp.Scope] = _serializeScope(scope)
      }
    };
  }

  /// <summary>
  /// Creates a delta containing only differences between previous and current scope.
  /// Returns null if nothing changed (scopes are equivalent).
  /// </summary>
  /// <param name="previous">The previous scope context (can be null for first hop).</param>
  /// <param name="current">The current scope context (required).</param>
  /// <returns>A delta containing only changes, or null if nothing changed.</returns>
  /// <exception cref="ArgumentNullException">Thrown when current is null.</exception>
  public static ScopeDelta? CreateDelta(IScopeContext? previous, IScopeContext current) {
    ArgumentNullException.ThrowIfNull(current);

    var values = new Dictionary<ScopeProp, JsonElement>();
    var collections = new Dictionary<ScopeProp, CollectionChanges>();

    // Compare PerspectiveScope
    if (!_scopesEqual(previous?.Scope, current.Scope)) {
      values[ScopeProp.Scope] = _serializeScope(current.Scope);
    }

    // Compare Roles
    var rolesChanges = _createCollectionChanges(
      previous?.Roles,
      current.Roles,
      r => r
    );
    if (rolesChanges.HasChanges) {
      collections[ScopeProp.Roles] = rolesChanges;
    }

    // Compare Permissions
    var permsChanges = _createCollectionChanges(
      previous?.Permissions,
      current.Permissions,
      p => p.Value
    );
    if (permsChanges.HasChanges) {
      collections[ScopeProp.Perms] = permsChanges;
    }

    // Compare SecurityPrincipals
    var principalsChanges = _createCollectionChanges(
      previous?.SecurityPrincipals,
      current.SecurityPrincipals,
      p => p.Value
    );
    if (principalsChanges.HasChanges) {
      collections[ScopeProp.Principals] = principalsChanges;
    }

    // Compare Claims
    var claimsChanges = _createClaimsChanges(previous?.Claims, current.Claims);
    if (claimsChanges.HasChanges) {
      collections[ScopeProp.Claims] = claimsChanges;
    }

    // Compare ActualPrincipal
    if (previous?.ActualPrincipal != current.ActualPrincipal) {
      values[ScopeProp.Actual] = _serializeString(current.ActualPrincipal);
    }

    // Compare EffectivePrincipal
    if (previous?.EffectivePrincipal != current.EffectivePrincipal) {
      values[ScopeProp.Effective] = _serializeString(current.EffectivePrincipal);
    }

    // Compare ContextType
    if (previous?.ContextType != current.ContextType) {
      values[ScopeProp.Type] = _serializeInt((int)current.ContextType);
    }

    // Return null if nothing changed
    if (values.Count == 0 && collections.Count == 0) {
      return null;
    }

    return new ScopeDelta {
      Values = values.Count > 0 ? values : null,
      Collections = collections.Count > 0 ? collections : null
    };
  }

  /// <summary>
  /// Applies this delta to a previous scope context to produce a new scope.
  /// </summary>
  /// <param name="previous">The previous scope context (can be null).</param>
  /// <returns>A new ScopeContext with delta changes applied.</returns>
  public ScopeContext ApplyTo(ScopeContext? previous) {
    var (scope, actualPrincipal, effectivePrincipal, contextType) = _applyValueChanges(previous);
    var (roles, permissions, principals, claims) = _applyCollectionChanges(previous);

    return new ScopeContext {
      Scope = scope,
      Roles = roles,
      Permissions = permissions,
      SecurityPrincipals = principals,
      Claims = claims,
      ActualPrincipal = actualPrincipal,
      EffectivePrincipal = effectivePrincipal,
      ContextType = contextType
    };
  }

  private (PerspectiveScope Scope, string? ActualPrincipal, string? EffectivePrincipal, SecurityContextType ContextType) _applyValueChanges(
      ScopeContext? previous) {
    var scope = previous?.Scope ?? new PerspectiveScope();
    var actualPrincipal = previous?.ActualPrincipal;
    var effectivePrincipal = previous?.EffectivePrincipal;
    var contextType = previous?.ContextType ?? SecurityContextType.User;

    if (Values != null) {
      if (Values.TryGetValue(ScopeProp.Scope, out var scopeElement)) {
        scope = _deserializeScope(scopeElement);
      }
      if (Values.TryGetValue(ScopeProp.Actual, out var actualElement)) {
        actualPrincipal = _deserializeString(actualElement);
      }
      if (Values.TryGetValue(ScopeProp.Effective, out var effectiveElement)) {
        effectivePrincipal = _deserializeString(effectiveElement);
      }
      if (Values.TryGetValue(ScopeProp.Type, out var typeElement)) {
        contextType = (SecurityContextType)_deserializeInt(typeElement);
      }
    }

    return (scope, actualPrincipal, effectivePrincipal, contextType);
  }

  private (IReadOnlySet<string> Roles, IReadOnlySet<Permission> Permissions, IReadOnlySet<SecurityPrincipalId> Principals, IReadOnlyDictionary<string, string> Claims) _applyCollectionChanges(
      ScopeContext? previous) {
    IReadOnlySet<string> roles = previous?.Roles ?? new HashSet<string>();
    IReadOnlySet<Permission> permissions = previous?.Permissions ?? new HashSet<Permission>();
    IReadOnlySet<SecurityPrincipalId> principals = previous?.SecurityPrincipals ?? new HashSet<SecurityPrincipalId>();
    IReadOnlyDictionary<string, string> claims = previous?.Claims ?? new Dictionary<string, string>();

    if (Collections != null) {
      if (Collections.TryGetValue(ScopeProp.Roles, out var rolesChanges)) {
        roles = _applyChanges(roles, rolesChanges, s => s);
      }
      if (Collections.TryGetValue(ScopeProp.Perms, out var permsChanges)) {
        permissions = _applyChanges(permissions, permsChanges, s => new Permission(s));
      }
      if (Collections.TryGetValue(ScopeProp.Principals, out var principalsChanges)) {
        principals = _applyChanges(principals, principalsChanges, s => new SecurityPrincipalId(s));
      }
      if (Collections.TryGetValue(ScopeProp.Claims, out var claimsChanges)) {
        claims = _applyClaimsChanges(claims, claimsChanges);
      }
    }

    return (roles, permissions, principals, claims);
  }

  private static string? _deserializeString(JsonElement element) =>
    element.ValueKind == JsonValueKind.Null ? null : element.GetString();

  private static int _deserializeInt(JsonElement element) =>
    element.GetInt32();

  private static JsonElement _serializeString(string? value) {
    if (value == null) {
      using var doc = JsonDocument.Parse("null");
      return doc.RootElement.Clone();
    }
    // Properly escape the string value for JSON
    var escaped = JsonEncodedText.Encode(value);
    using var doc2 = JsonDocument.Parse($"\"{escaped}\"");
    return doc2.RootElement.Clone();
  }

  private static JsonElement _serializeInt(int value) {
    using var doc = JsonDocument.Parse(value.ToString(CultureInfo.InvariantCulture));
    return doc.RootElement.Clone();
  }

  private static bool _scopesEqual(PerspectiveScope? a, PerspectiveScope? b) {
    if (a == null && b == null) {
      return true;
    }
    if (a == null || b == null) {
      return false;
    }
    return a.TenantId == b.TenantId
        && a.UserId == b.UserId
        && a.CustomerId == b.CustomerId
        && a.OrganizationId == b.OrganizationId
        && a.AllowedPrincipals.SequenceEqual(b.AllowedPrincipals);
  }

  private static JsonElement _serializeScope(PerspectiveScope scope) {
    // Manual JSON construction to avoid AOT issues
    var sb = new StringBuilder();
    sb.Append('{');
    var first = true;

    _appendJsonStringProperty(sb, "t", scope.TenantId, ref first);
    _appendJsonStringProperty(sb, "u", scope.UserId, ref first);
    _appendJsonStringProperty(sb, "c", scope.CustomerId, ref first);
    _appendJsonStringProperty(sb, "o", scope.OrganizationId, ref first);
    _appendAllowedPrincipals(sb, scope.AllowedPrincipals, ref first);

    sb.Append('}');
    using var doc = JsonDocument.Parse(sb.ToString());
    return doc.RootElement.Clone();
  }

  private static void _appendJsonStringProperty(StringBuilder sb, string key, string? value, ref bool first) {
    if (value == null) {
      return;
    }
    if (!first) {
      sb.Append(',');
    }
    sb.Append('"').Append(key).Append("\":\"").Append(JsonEncodedText.Encode(value)).Append('"');
    first = false;
  }

  private static void _appendAllowedPrincipals(StringBuilder sb, List<string> principals, ref bool first) {
    if (principals.Count == 0) {
      return;
    }
    if (!first) {
      sb.Append(',');
    }
    sb.Append("\"ap\":[");
    for (var i = 0; i < principals.Count; i++) {
      if (i > 0) {
        sb.Append(',');
      }
      sb.Append('"').Append(JsonEncodedText.Encode(principals[i])).Append('"');
    }
    sb.Append(']');
  }

  private static PerspectiveScope _deserializeScope(JsonElement element) {
    var scope = new PerspectiveScope();

    if (element.TryGetProperty("t", out var t) && t.ValueKind != JsonValueKind.Null) {
      scope.TenantId = t.GetString();
    }
    if (element.TryGetProperty("u", out var u) && u.ValueKind != JsonValueKind.Null) {
      scope.UserId = u.GetString();
    }
    if (element.TryGetProperty("c", out var c) && c.ValueKind != JsonValueKind.Null) {
      scope.CustomerId = c.GetString();
    }
    if (element.TryGetProperty("o", out var o) && o.ValueKind != JsonValueKind.Null) {
      scope.OrganizationId = o.GetString();
    }
    if (element.TryGetProperty("ap", out var ap) && ap.ValueKind == JsonValueKind.Array) {
      foreach (var item in ap.EnumerateArray()) {
        var val = item.GetString();
        if (val != null) {
          scope.AllowedPrincipals.Add(val);
        }
      }
    }

    return scope;
  }

  private static JsonElement _serializeStringArray(IEnumerable<string> values) {
    var sb = new StringBuilder();
    sb.Append('[');
    var first = true;
    foreach (var value in values) {
      if (!first) {
        sb.Append(',');
      }
      sb.Append('"').Append(JsonEncodedText.Encode(value)).Append('"');
      first = false;
    }
    sb.Append(']');
    using var doc = JsonDocument.Parse(sb.ToString());
    return doc.RootElement.Clone();
  }

  private static List<string> _deserializeStringArray(JsonElement element) {
    var result = new List<string>();
    if (element.ValueKind == JsonValueKind.Array) {
      foreach (var item in element.EnumerateArray()) {
        var val = item.GetString();
        if (val != null) {
          result.Add(val);
        }
      }
    }
    return result;
  }

  private static CollectionChanges _createCollectionChanges<T>(
      IReadOnlySet<T>? previous,
      IReadOnlySet<T> current,
      Func<T, string> toStringFunc) {
    previous ??= new HashSet<T>();

    var added = current.Except(previous).ToList();
    var removed = previous.Except(current).ToList();

    // If no previous, use Set operation for full replacement
    if (previous.Count == 0 && current.Count > 0) {
      return new CollectionChanges {
        Set = _serializeStringArray(current.Select(toStringFunc))
      };
    }

    // If all removed and new ones added, use Set
    if (removed.Count == previous.Count && added.Count == current.Count && current.Count > 0) {
      return new CollectionChanges {
        Set = _serializeStringArray(current.Select(toStringFunc))
      };
    }

    // Otherwise use Add/Remove operations
    JsonElement? addElement = added.Count > 0
      ? _serializeStringArray(added.Select(toStringFunc))
      : null;
    JsonElement? removeElement = removed.Count > 0
      ? _serializeStringArray(removed.Select(toStringFunc))
      : null;

    return new CollectionChanges {
      Add = addElement,
      Remove = removeElement
    };
  }

  private static JsonElement _serializeStringDictionary(Dictionary<string, string> dict) {
    var sb = new StringBuilder();
    sb.Append('{');
    var first = true;
    foreach (var kvp in dict) {
      if (!first) {
        sb.Append(',');
      }
      sb.Append('"').Append(JsonEncodedText.Encode(kvp.Key)).Append("\":\"");
      sb.Append(JsonEncodedText.Encode(kvp.Value)).Append('"');
      first = false;
    }
    sb.Append('}');
    using var doc = JsonDocument.Parse(sb.ToString());
    return doc.RootElement.Clone();
  }

  private static Dictionary<string, string> _deserializeStringDictionary(JsonElement element) {
    var result = new Dictionary<string, string>();
    if (element.ValueKind == JsonValueKind.Object) {
      foreach (var prop in element.EnumerateObject()) {
        var val = prop.Value.GetString();
        if (val != null) {
          result[prop.Name] = val;
        }
      }
    }
    return result;
  }

  private static CollectionChanges _createClaimsChanges(
      IReadOnlyDictionary<string, string>? previous,
      IReadOnlyDictionary<string, string> current) {
    previous ??= new Dictionary<string, string>();

    // Claims are key-value, so we track added/modified and removed keys
    var added = new Dictionary<string, string>();
    var removed = new List<string>();

    foreach (var kvp in current) {
      if (!previous.TryGetValue(kvp.Key, out var prevValue) || prevValue != kvp.Value) {
        added[kvp.Key] = kvp.Value;
      }
    }

    foreach (var key in previous.Keys) {
      if (!current.ContainsKey(key)) {
        removed.Add(key);
      }
    }

    if (added.Count == 0 && removed.Count == 0) {
      return default;
    }

    // For claims, we use Add for new/modified and Remove for deleted keys
    return new CollectionChanges {
      Add = added.Count > 0 ? _serializeStringDictionary(added) : null,
      Remove = removed.Count > 0 ? _serializeStringArray(removed) : null
    };
  }

  private static HashSet<T> _applyChanges<T>(
      IReadOnlySet<T> previous,
      CollectionChanges changes,
      Func<string, T> fromStringFunc) {
    // Set takes precedence
    if (changes.Set.HasValue) {
      var values = _deserializeStringArray(changes.Set.Value);
      return [.. values.Select(fromStringFunc)];
    }

    var result = new HashSet<T>(previous);

    // Apply Remove first
    if (changes.Remove.HasValue) {
      var toRemove = _deserializeStringArray(changes.Remove.Value);
      foreach (var item in toRemove.Select(fromStringFunc)) {
        result.Remove(item);
      }
    }

    // Then Apply Add
    if (changes.Add.HasValue) {
      var toAdd = _deserializeStringArray(changes.Add.Value);
      foreach (var item in toAdd.Select(fromStringFunc)) {
        result.Add(item);
      }
    }

    return result;
  }

  private static Dictionary<string, string> _applyClaimsChanges(
      IReadOnlyDictionary<string, string> previous,
      CollectionChanges changes) {
    var result = new Dictionary<string, string>(previous);

    // Remove first
    if (changes.Remove.HasValue) {
      var toRemove = _deserializeStringArray(changes.Remove.Value);
      foreach (var key in toRemove) {
        result.Remove(key);
      }
    }

    // Then Add/Update
    if (changes.Add.HasValue) {
      var toAdd = _deserializeStringDictionary(changes.Add.Value);
      foreach (var kvp in toAdd) {
        result[kvp.Key] = kvp.Value;
      }
    }

    return result;
  }
}
