// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// One assembly's perspective registry payload: the JSON array <c>reconcile_perspective_registry</c> reads, and the
/// service it registers the perspectives under.
/// </summary>
/// <param name="ServiceName">The service that owns the perspectives (the declaring assembly's name).</param>
/// <param name="Json">The payload: one entry per perspective, keyed by the model's CLR type name.</param>
/// <docs>fundamentals/perspectives/row-retention#dapper-driver</docs>
public sealed record PerspectiveRegistryPayload(string ServiceName, string Json);

/// <summary>
/// The perspective registry payloads the host's assemblies registered at load. The driver-neutral schema generator
/// emits a <c>[ModuleInitializer]</c> in each assembly that declares perspectives, and a driver that has no
/// generated payload of its own (the Dapper driver) reconciles these into <c>wh_perspective_registry</c> at
/// startup, which is what row retention enrolls from (#1548).
/// </summary>
/// <docs>fundamentals/perspectives/row-retention#dapper-driver</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRegistryPayloadsTests.cs</tests>
public static class PerspectiveRegistryPayloads {
  private static readonly ConcurrentDictionary<string, string> _payloads = new(StringComparer.Ordinal);

  /// <summary>
  /// Registers <paramref name="serviceName"/>'s payload, replacing any it registered before. Called from generated
  /// module initializers.
  /// </summary>
  /// <param name="serviceName">The service that owns the perspectives.</param>
  /// <param name="json">The payload.</param>
  public static void Register(string serviceName, string json) {
    ArgumentException.ThrowIfNullOrEmpty(serviceName);
    ArgumentException.ThrowIfNullOrEmpty(json);
    _payloads[serviceName] = json;
  }

  /// <summary>Every registered payload, one per service.</summary>
  public static IReadOnlyList<PerspectiveRegistryPayload> Registered =>
    [.. _payloads.Select(p => new PerspectiveRegistryPayload(p.Key, p.Value))];
}
