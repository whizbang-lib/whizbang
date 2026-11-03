// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Whizbang.Core.Startup;

namespace Whizbang.Testing.Workers;

/// <summary>
/// Finds a host's workers among its hosted services. A worker registered with its start point
/// (<c>AddWhizbangWorker</c>, <c>AddWhizbangWorkerAtSchemaReady</c>) is hosted inside a <see cref="GatedHostedService"/>,
/// so a lookup by type over <c>GetServices&lt;IHostedService&gt;()</c> finds the wrapper and never the worker. These
/// look through the wrapper, and are the one way a test or a fixture finds a hosted worker.
/// </summary>
/// <docs>testing/multi-service-harness</docs>
/// <tests>tests/Whizbang.Testing.Tests/Workers/HostedWorkersTests.cs</tests>
public static class HostedWorkers {
  /// <summary>
  /// The provider's hosted services, each worker hosted with its start point given as the worker itself.
  /// </summary>
  /// <param name="services">The host's service provider.</param>
  /// <returns>The hosted services, in registration order, with every stage wrapper replaced by the worker it hosts.</returns>
  public static IEnumerable<IHostedService> GetHostedWorkers(this IServiceProvider services) =>
    services.GetServices<IHostedService>().Select(static hosted => hosted is GatedHostedService gated ? gated.Service : hosted);

  /// <summary>
  /// The first hosted service of type <typeparamref name="TWorker"/>, looking through the stage wrapper, or
  /// <see langword="null"/> when the host does not host one.
  /// </summary>
  /// <typeparam name="TWorker">The worker type.</typeparam>
  /// <param name="services">The host's service provider.</param>
  /// <returns>The hosted worker, or <see langword="null"/>.</returns>
  public static TWorker? GetHostedWorker<TWorker>(this IServiceProvider services) where TWorker : class =>
    services.GetHostedWorkers().OfType<TWorker>().FirstOrDefault();
}
