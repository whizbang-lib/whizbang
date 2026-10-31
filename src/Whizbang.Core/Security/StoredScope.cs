// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Security;

/// <summary>
/// Reads back the scope a stored event row kept in its scope column, whole: tenant, user, customer,
/// organization, allowed principals, extensions and the system and declared-unscoped markers.
/// </summary>
/// <remarks>
/// <para>
/// A stored event is read back by a replay, a rebuild, a redelivery or reconciliation, and each of those paths
/// rebuilds the event's envelope from the row. A path that rebuilt the scope from the tenant and user alone
/// handed a receptor or perspective less than the original dispatch had, so every read-back path restores the
/// scope through here and matches what the event store restores.
/// </para>
/// <para>
/// Two stored formats are read: the short-key <see cref="PerspectiveScope"/> format every current writer uses,
/// and the snake_case <c>tenant_id</c>/<c>user_id</c> format older rows carry.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/message-security#hop-scope</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/StoredScopeTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStoredScopeReadBackTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreStoredScopeReadBackTests.cs</tests>
public static class StoredScope {

  /// <summary>
  /// Reads a stored scope column, or <see langword="null"/> when it names no scope.
  /// </summary>
  /// <param name="scopeJson">The scope column's JSON, or null for a row stored without one.</param>
  /// <param name="options">The serializer options the row is read with.</param>
  /// <returns>The stored scope, or null when the column is absent, JSON null, or names nothing.</returns>
  /// <exception cref="JsonException">The column is JSON that neither stored format can read, such as an array.</exception>
  public static PerspectiveScope? Read(string? scopeJson, JsonSerializerOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (string.IsNullOrEmpty(scopeJson)) {
      return null;
    }
    var scope = _readShortKeys(scopeJson, options);
    return scope is null || ScopeDelta.NamesNothing(scope) ? _readByKey(scopeJson, options) : scope;
  }

  /// <summary>
  /// Restores a stored scope onto the first hop of an envelope read back from the store, synthesizing that hop
  /// when the row recorded none. A first hop that already carries a scope keeps it, and an absent or empty
  /// scope changes nothing.
  /// </summary>
  /// <param name="hops">The envelope's hops, as read from the row.</param>
  /// <param name="scope">The stored scope, from <see cref="Read"/>.</param>
  public static void RestoreInto(List<MessageHop> hops, PerspectiveScope? scope) {
    ArgumentNullException.ThrowIfNull(hops);
    if (hops.Count > 0 && hops[0].Scope is not null) {
      return;
    }
    if (ScopeDelta.FromPerspectiveScope(scope) is not { } delta) {
      return;
    }
    // A row read back from the store keeps its scope in a column and often no hops; GetCurrentScope walks
    // hops, so the scope needs one to live on. An event stored without a scope gets none (returned above).
    if (hops.Count == 0) {
      hops.Add(new MessageHop { Type = HopType.Current, ServiceInstance = ServiceInstanceInfo.Unknown, Scope = delta });
      return;
    }
    hops[0] = hops[0] with { Scope = delta };
  }

  private static PerspectiveScope? _readShortKeys(string scopeJson, JsonSerializerOptions options) {
    // Ask rather than demand: options without PerspectiveScope fall through to the keyed reader.
    if (!options.TryGetTypeInfo(typeof(PerspectiveScope), out var typeInfo)) {
      return null;
    }
    try {
      return JsonSerializer.Deserialize(scopeJson, typeInfo) as PerspectiveScope;
    } catch (JsonException) {
      // Not the short-key shape (a field of the wrong type); the keyed reader below is the fallback for it.
      return null;
    }
  }

  // Reads the id fields by key, short or snake_case: rows in the older format, and options without PerspectiveScope.
  private static PerspectiveScope? _readByKey(string scopeJson, JsonSerializerOptions options) {
    var typeInfo = options.GetTypeInfo(typeof(Dictionary<string, JsonElement>));
    if (JsonSerializer.Deserialize(scopeJson, typeInfo) is not Dictionary<string, JsonElement> keys) {
      return null;
    }
    var scope = new PerspectiveScope {
      TenantId = _string(keys, "t") ?? _string(keys, "tenant_id"),
      UserId = _string(keys, "u") ?? _string(keys, "user_id"),
      CustomerId = _string(keys, "c"),
      OrganizationId = _string(keys, "o"),
    };
    return ScopeDelta.NamesNothing(scope) ? null : scope;
  }

  private static string? _string(Dictionary<string, JsonElement> keys, string key) =>
    keys.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
