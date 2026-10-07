// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Naming;

/// <summary>
/// Central, reusable name-derivation helpers Whizbang uses to keep its
/// turnkey wiring coherent across the source generator, runtime DI, and
/// any user code that wants to follow the same convention.
/// </summary>
/// <remarks>
/// <para>
/// The EF Core source generator and the runtime turnkey path must agree on every name here: when
/// they once derived the connection string name separately, EF Core and the notification workers
/// read different keys and LISTEN/NOTIFY silently fell back to the pooled connection. The generator
/// keeps a copy (it cannot reference this assembly); a generator test and a turnkey test pin both to "db".
/// </para>
/// </remarks>
/// <docs>operations/configuration/configuration-reference#connectionstrings-conventions</docs>
public static class WhizbangNamingConvention {

#pragma warning disable CA1707 // project convention: public const strings use UPPER_CASE with underscores
  /// <summary>
  /// The connection string a database reads when its <c>DbContext</c> names none:
  /// <c>ConnectionStrings:db</c>, with <c>db-direct</c> for notifications and <c>db-init</c> for
  /// schema initialization. The same in every service, since each service has its own configuration.
  /// </summary>
  public const string DEFAULT_CONNECTION_STRING_NAME = "db";
#pragma warning restore CA1707
}
