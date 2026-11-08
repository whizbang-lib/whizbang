// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Whizbang.Core.DependencyInjection;

namespace Whizbang.Core.RunControl;

/// <summary>
/// DI wiring for the coordinated lifecycle plane: registers the <see cref="WhizbangLifecycleCoordinator"/>
/// (over every registered <see cref="IWhizbangRunControl"/> participant), the
/// <see cref="WhizbangLifecycleOptions"/> (ack timeout + fault record window), and the shared
/// <see cref="IWhizbangLifecycleState"/>. Each managed subsystem registers its participant via
/// <see cref="AddWhizbangRunControlAdapter{TControl}"/>; both faces read the one lifecycle state.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
/// <tests>tests/Whizbang.Core.Tests/RunControl/WhizbangRunControlDiTests.cs:AddWhizbangRunControl_RegistersCoordinatorAndStateAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/RunControl/WhizbangRunControlDiTests.cs:AddWhizbangRunControl_BroadcastsToRegisteredAdaptersAsync</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/RunControl/WhizbangInstanceExitTests.cs:TheRunControlRegistration_ProvidesOneExitPath_AndTheProcessExitCodeAsync</tests>
public static class WhizbangRunControlServiceCollectionExtensions {
  /// <summary>Registers the lifecycle coordinator + options + state (idempotent). <paramref name="configure"/> tunes the ack timeout / fault window.</summary>
  public static IServiceCollection AddWhizbangRunControl(
      this IServiceCollection services, Action<WhizbangLifecycleOptions>? configure = null) {
    ArgumentNullException.ThrowIfNull(services);
    var options = new WhizbangLifecycleOptions();
    configure?.Invoke(options);
    // #1014: Whizbang:Lifecycle binds over the lambda's values when the options first resolve.
    services.TryAddSingleton(sp => Configuration.ProcessWideOptionsBinding.BindLifecycle(sp, options));
    services.TryAddSingleton(static sp => new WhizbangLifecycleCoordinator(
      sp.GetServices<IWhizbangRunControl>(),
      sp.GetRequiredService<WhizbangLifecycleOptions>(),
      sp.GetService<TimeProvider>()));
    services.TryAddSingleton<IWhizbangLifecycleState>(static sp => new WhizbangLifecycleState(
      sp.GetRequiredService<WhizbangLifecycleCoordinator>(),
      sp.GetRequiredService<WhizbangLifecycleOptions>(),
      sp.GetService<TimeProvider>()));
    // #2045: the instance's one run permit, and the one path that closes and reopens it. Every claiming worker, the
    // transport consumer and the role elector take this permit; held work and roles are released through the
    // registered IReleasesHeldWork and IReleasesHeldRoles, resolved when the path is first used.
    services.TryAddSingleton<WhizbangRunPermit>();
    services.TryAddSingleton<IWhizbangRunController>(static sp => new WhizbangRunController(
      sp.GetRequiredService<WhizbangRunPermit>(),
      sp.GetRequiredService<IWhizbangLifecycleState>(),
      sp.GetServices<IReleasesHeldWork>(),
      sp.GetServices<IReleasesHeldRoles>(),
      sp.GetRequiredService<WhizbangLifecycleOptions>(),
      sp.GetService<TimeProvider>(),
      sp.GetLoggerOrNullLogger<WhizbangRunController>()));
    TryAddWhizbangInstanceExit(services);
    services.TryAddSingleton<IWhizbangKillswitch>(static sp => new WhizbangKillswitch(
      sp.GetRequiredService<IWhizbangRunController>(),
      sp.GetRequiredService<IWhizbangLifecycleState>(),
      sp.GetRequiredService<WhizbangLifecycleCoordinator>(),
      sp.GetRequiredService<IWhizbangInstanceExit>()));
    return services;
  }

  /// <summary>
  /// Registers the one exit path (#2126) and the one place the exit code is set (idempotent). The run-control
  /// registration and every other registration that can end the process (the capability enforcer) call it, so no host
  /// that can exit lacks the path. The exit halts through the run-control path when the host registers one.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <returns>The same collection.</returns>
  /// <docs>operations/startup/instance-exits</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/RunControl/WhizbangInstanceExitTests.cs:TheRunControlRegistration_ProvidesOneExitPath_AndTheProcessExitCodeAsync</tests>
  public static IServiceCollection TryAddWhizbangInstanceExit(this IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);
    // A test host registers its own IProcessExitCode first so the test process keeps its own exit code.
    services.TryAddSingleton<IProcessExitCode>(ProcessExitCode.Instance);
    services.TryAddSingleton<IWhizbangInstanceExit>(static sp => new WhizbangInstanceExit(
      sp,
      sp.GetService<IWhizbangRunController>(),
      sp.GetRequiredService<IProcessExitCode>(),
      sp.GetService<TimeProvider>(),
      sp.GetLoggerOrNullLogger<WhizbangInstanceExit>()));
    return services;
  }

  /// <summary>Registers one managed-resource run-control participant. Every registered participant is driven by the coordinator.</summary>
  public static IServiceCollection AddWhizbangRunControlAdapter<
      [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TControl>(
      this IServiceCollection services)
      where TControl : class, IWhizbangRunControl {
    ArgumentNullException.ThrowIfNull(services);
    services.AddSingleton<IWhizbangRunControl, TControl>();
    return services;
  }
}
