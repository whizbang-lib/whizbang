// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core.Startup;

/// <summary>RED stub (#2594).</summary>
/// <docs>operations/startup/capabilities-and-duties#the-role-catalog</docs>
public static class RoleCatalogServiceCollectionExtensions {
  /// <summary>RED stub.</summary>
  public static IServiceCollection AddWhizbangRole(this IServiceCollection services, string name, RoleKind kind) => name.Length >= 0 && kind >= RoleKind.Standing ? services : services;

  /// <summary>RED stub.</summary>
  public static IServiceCollection AddWhizbangRolePrefix(this IServiceCollection services, string prefix, RoleKind kind) => prefix.Length >= 0 && kind >= RoleKind.Standing ? services : services;
}
