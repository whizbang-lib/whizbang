// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// One instance's signal bus over Postgres, as a host builds it: the shared notify connection, the Postgres transport on
/// it, and the bus over the transport (#2046). Everything that coordinates over the database is a subscriber of this bus.
/// </summary>
internal sealed class PgSignalBusStack : IAsyncDisposable {
  private bool _disposed;

  private PgSignalBusStack(PgSharedNotifyConnection shared, PostgresSignalTransport transport, IServiceInstanceProvider instance) {
    Shared = shared;
    Transport = transport;
    Instance = instance;
    Bus = new SignalBus([transport], []);
  }

  /// <summary>The instance's one LISTEN connection.</summary>
  public PgSharedNotifyConnection Shared { get; }

  /// <summary>The bus's Postgres transport.</summary>
  public PostgresSignalTransport Transport { get; }

  /// <summary>The bus subscribers use.</summary>
  public SignalBus Bus { get; }

  /// <summary>The instance this stack belongs to.</summary>
  public IServiceInstanceProvider Instance { get; }

  /// <summary>Builds a stack for <paramref name="instance"/> (a fresh identity when null), not yet started.</summary>
  public static PgSignalBusStack Create(string connectionString, IServiceInstanceProvider? instance = null) {
    var options = Options.Create(new WhizbangNotificationOptions {
      DirectConnectionString = connectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
    });
    var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    instance ??= new ServiceInstanceProvider(configuration);
    var shared = new PgSharedNotifyConnection(options, configuration, instance, NullLogger<PgSharedNotifyConnection>.Instance);
    var transport = new PostgresSignalTransport(options, configuration, shared, instance, NullLogger<PostgresSignalTransport>.Instance);
    return new PgSignalBusStack(shared, transport, instance);
  }

  /// <summary>Starts the connection and the bus, the order a host starts them in.</summary>
  public async Task StartAsync(CancellationToken cancellationToken) {
    await ((IHostedService)Shared).StartAsync(cancellationToken);
    await Bus.StartAsync(cancellationToken);
  }

  /// <summary>Completes once the connection has issued its LISTEN on <paramref name="channel"/>: the signal a test emits after.</summary>
  public Task ListenedAsync(string channel, CancellationToken cancellationToken) =>
    Shared.WaitForChannelListenedAsync(channel, cancellationToken);

  /// <inheritdoc />
  public async ValueTask DisposeAsync() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    Transport.Dispose();
    await ((IHostedService)Shared).StopAsync(CancellationToken.None);
    Shared.Dispose();
  }
}
