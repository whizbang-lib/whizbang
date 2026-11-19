// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Hosting;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Startup;

/// <summary>
/// Starts a hosted service when the schema-ready gate opens, at the end of <see cref="StartupStage.Coordinate"/> and
/// before <see cref="StartupStage.AcceptingCommands"/> is recorded (#1636): the start point of the work that keeps an
/// instance alive and coordinated while it is not yet eligible for work, such as the heartbeat.
/// </summary>
/// <remarks>
/// A host whose schema is not initialized by Whizbang has no gate to wait for; its registration hands this
/// <see cref="SchemaReadyGate.AlreadyReady"/>, so the service starts with the host. Register through <see cref="WhizbangWorkerServiceCollectionExtensions.AddWhizbangWorkerAtSchemaReady{TWorker}(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/>.
/// </remarks>
/// <typeparam name="TService">The hosted service it starts.</typeparam>
/// <docs>operations/startup/startup-status#what-runs-in-each-stage</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Startup/WhizbangWorkerRegistrationTests.cs</tests>
public sealed class SchemaReadyGatedService<TService> : GatedHostedService where TService : class, IHostedService {
  private readonly ISchemaReadyGate _schemaReady;

  /// <summary>Creates the wrapper.</summary>
  /// <param name="inner">The service to start when the schema is ready.</param>
  /// <param name="schemaReady">The schema-ready gate; one already open starts the service with the host.</param>
  public SchemaReadyGatedService(TService inner, ISchemaReadyGate schemaReady) : base(inner) {
    ArgumentNullException.ThrowIfNull(schemaReady);
    Inner = inner;
    _schemaReady = schemaReady;
  }

  /// <summary>The service this starts.</summary>
  public TService Inner { get; }

  /// <inheritdoc />
  public override string StartPoint => "schema ready";

  /// <inheritdoc />
  protected override Task WaitToStartAsync(CancellationToken stoppingToken) =>
    _schemaReady.WaitForReadyAsync(stoppingToken);
}
