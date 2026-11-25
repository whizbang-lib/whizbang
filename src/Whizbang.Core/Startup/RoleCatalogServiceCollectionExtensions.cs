// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Whizbang.Core.Startup;

/// <summary>Declares the roles an instance may vote for (#2594).</summary>
/// <docs>operations/startup/capabilities-and-duties#the-role-catalog</docs>
/// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Startup/RoleCatalogTests.cs:AddWhizbangRole_DeclaresIt_AndTheHolderLoopAndHealthManageItAsync</tests>
public static class RoleCatalogServiceCollectionExtensions {
  /// <summary>
  /// Declares <paramref name="name"/> as a role. A standing role also joins the roster the holder loop holds and the
  /// <c>roles</c> health component reports (<see cref="RoleAssignmentOptions.Roles"/>); an episodic one is held only by
  /// the work that asks for it, so a vacancy is never reported. A startup step whose
  /// <see cref="StartupStepDescriptor.RequiredCapability"/> is a duty of the host's own declares it here; the role
  /// elector refuses a role nothing declared.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="name">The role.</param>
  /// <param name="kind">How it is held.</param>
  /// <returns>The same collection.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
  /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty or white space.</exception>
  public static IServiceCollection AddWhizbangRole(this IServiceCollection services, string name, RoleKind kind) {
    ArgumentNullException.ThrowIfNull(services);
    var declaration = new RoleDeclaration(name, kind);
    _declare(services, declaration);
    if (kind == RoleKind.Standing) {
      services.AddOptions<RoleAssignmentOptions>().Configure(options => options.Roles.Add(declaration.Name));
    }
    return services;
  }

  /// <summary>
  /// Declares every role that starts with <paramref name="prefix"/>, for a family of roles named per item (one per
  /// table, say). A family is held per member and is not put in the managed roster.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="prefix">The start every role of the family has.</param>
  /// <param name="kind">How each is held.</param>
  /// <returns>The same collection.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
  /// <exception cref="ArgumentException"><paramref name="prefix"/> is null, empty or white space.</exception>
  public static IServiceCollection AddWhizbangRolePrefix(this IServiceCollection services, string prefix, RoleKind kind) {
    ArgumentNullException.ThrowIfNull(services);
    _declare(services, new RoleDeclaration(prefix, kind, isPrefix: true));
    return services;
  }

  /// <summary>
  /// Registers the <see cref="RoleCatalog"/>, built from the framework's roles and every declaration, once. A driver's
  /// role elector calls it, so the catalog exists whether or not the host declared a role of its own.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <returns>The same collection.</returns>
  public static IServiceCollection AddWhizbangRoleCatalog(this IServiceCollection services) {
    services.TryAddSingleton(sp => new RoleCatalog(sp.GetServices<RoleDeclaration>()));
    return services;
  }

  private static void _declare(IServiceCollection services, RoleDeclaration declaration) {
    services.AddSingleton(declaration);
    services.AddWhizbangRoleCatalog();
  }
}
