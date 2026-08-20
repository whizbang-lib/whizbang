using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

namespace Whizbang.Core.Transports;

/// <summary>
/// AOT-safe configuration binder for a transport's TransportNamespace connection map (transport
/// traffic classes, topology arc phase 8). Reads
/// <c>&lt;transportSection&gt;:Namespaces:&lt;namespaceKey&gt;</c> by explicit key enumeration —
/// no reflection-based options binder — and resolves each value through the
/// <c>ConnectionStrings:*</c> convention so the connection VALUE (a secret) never has to live
/// in the Whizbang configuration section.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shape.</b> Each child key under <c>Namespaces</c> is a TransportNamespace key; its value
/// names a <c>ConnectionStrings</c> entry. A value with no matching connection string is used
/// verbatim, so hosts that project a whole connection string into the section (environment
/// variable, secret-store projection) still work:
/// </para>
/// <code>
/// {
///   "ConnectionStrings": {
///     "servicebus":      "Endpoint=sb://main...",
///     "servicebus-bulk": "Endpoint=sb://bulk..."
///   },
///   "Whizbang": { "Transports": { "AzureServiceBus": { "Namespaces": {
///     "default": "servicebus",
///     "bulk":    "servicebus-bulk"
///   } } } }
/// }
/// </code>
/// <para>
/// Environment-variable form: <c>Whizbang__Transports__AzureServiceBus__Namespaces__bulk=servicebus-bulk</c>.
/// The transports apply configuration OVER the code map (last-wins per key, the
/// post-configure idiom), so an operator can add or re-point a traffic-class namespace without
/// a redeploy.
/// </para>
/// </remarks>
/// <docs>messaging/transports/transports#transport-namespaces</docs>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusNamespaceRoutingRegistrationTests.cs:NamespaceConnectionStrings_BindsFromConfigurationViaConnectionStringsConventionAsync</tests>
public static class TransportNamespaceConnectionStrings {
#pragma warning disable CA1707 // project convention: public const strings use UPPER_CASE with underscores
  /// <summary>The child section, under a transport's own configuration section, that carries the namespace map.</summary>
  public const string NAMESPACES_SUBSECTION = "Namespaces";
#pragma warning restore CA1707

  /// <summary>
  /// Reads the TransportNamespace connection map from
  /// <paramref name="transportConfigurationSection"/>'s <c>Namespaces</c> child section.
  /// </summary>
  /// <param name="configuration">The host configuration (may be null).</param>
  /// <param name="transportConfigurationSection">
  /// The transport's configuration section path, e.g. <c>"Whizbang:Transports:AzureServiceBus"</c>.
  /// </param>
  /// <returns>
  /// TransportNamespace key → resolved connection string, ordinal-keyed. Empty when the
  /// configuration or the section is absent. Entries with a blank key or blank value are
  /// skipped — a half-written override must not mint a nameless namespace.
  /// </returns>
  public static IReadOnlyDictionary<string, string> Read(
      IConfiguration? configuration, string transportConfigurationSection) {
    ArgumentException.ThrowIfNullOrWhiteSpace(transportConfigurationSection);

    var map = new Dictionary<string, string>(StringComparer.Ordinal);
    var section = configuration?.GetSection($"{transportConfigurationSection}:{NAMESPACES_SUBSECTION}");
    if (section is null || !section.Exists()) {
      return map;
    }

    foreach (var child in section.GetChildren()) {
      if (string.IsNullOrWhiteSpace(child.Key) || string.IsNullOrWhiteSpace(child.Value)) {
        continue;
      }

      // ConnectionStrings:* first (the value NAMES a connection), falling back to the literal
      // value so a projected secret works without a second indirection.
      var resolved = configuration!.GetConnectionString(child.Value);
      map[child.Key] = string.IsNullOrWhiteSpace(resolved) ? child.Value : resolved;
    }

    return map;
  }

  /// <summary>
  /// Merges <paramref name="configured"/> OVER <paramref name="code"/> (configuration wins per
  /// key) and validates the result: at least one entry, the reserved
  /// <see cref="TransportNamespaces.DefaultKey"/> present, and no blank connection value.
  /// </summary>
  /// <param name="code">The map supplied by the registration callback.</param>
  /// <param name="configured">The map read from configuration.</param>
  /// <param name="parameterName">Parameter name for the thrown <see cref="ArgumentException"/>.</param>
  /// <returns>The merged, validated map (ordinal-keyed).</returns>
  /// <exception cref="ArgumentException">The merged map is empty, lacks the default key, or carries a blank value.</exception>
  public static IReadOnlyDictionary<string, string> MergeAndValidate(
      IReadOnlyDictionary<string, string> code,
      IReadOnlyDictionary<string, string> configured,
      string parameterName) {
    ArgumentNullException.ThrowIfNull(code);
    ArgumentNullException.ThrowIfNull(configured);

    var merged = new Dictionary<string, string>(code, StringComparer.Ordinal);
    foreach (var (key, value) in configured) {
      merged[key] = value;
    }

    if (merged.Count == 0) {
      throw new ArgumentException(
        "The TransportNamespace connection map is empty. Supply at least the reserved "
        + $"'{TransportNamespaces.DefaultKey}' namespace — it is the connection every unrouted message uses.",
        parameterName);
    }

    if (!merged.ContainsKey(TransportNamespaces.DefaultKey)) {
      throw new ArgumentException(
        $"The TransportNamespace connection map has no '{TransportNamespaces.DefaultKey}' key (found: "
        + $"{string.Join(", ", merged.Keys)}). Every message whose type carries no routing binding — and every "
        + $"binding whose key this host has no connection for — rides '{TransportNamespaces.DefaultKey}', so it is "
        + "required. Add it with the connection you use today; the remaining keys are the traffic classes.",
        parameterName);
    }

    foreach (var (key, value) in merged) {
      if (string.IsNullOrWhiteSpace(value)) {
        throw new ArgumentException(
          $"The TransportNamespace connection map entry '{key}' has no connection string. A namespace without a "
          + "connection cannot open a client — remove the key, or point it at a real connection.",
          parameterName);
      }
    }

    return merged;
  }
}
