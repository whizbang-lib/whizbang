// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Whizbang.Core;
using Whizbang.Core.MultiStream;
using Whizbang.Core.Notifications;

namespace Whizbang.Data.Postgres.MultiStream;

/// <summary>
/// Wires multi-stream commands on Postgres: the store (over the null default an application's handler registration
/// leaves, in either registration order), the worker, its subscription to new-run notices, and its options.
/// </summary>
/// <docs>fundamentals/dispatcher/multi-stream-commands#registration</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandPostgresRegistrationTests.cs:AddWhizbangPostgresNotifications_WiresTheStoreAndTheWorkerAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandPostgresRegistrationTests.cs:AHandlerRegisteredBeforeTheDriver_StillGetsThePostgresStoreAsync</tests>
internal static class MultiStreamCommandRegistration {
  internal static IServiceCollection AddWhizbangMultiStreamCommands(this IServiceCollection services) {
    services.AddOptions<MultiStreamCommandOptions>();
    services.TryAddEnumerable(ServiceDescriptor.Singleton<
      IConfigureOptions<MultiStreamCommandOptions>, ConfigureMultiStreamCommandOptionsFromConfiguration>());
    services.TryAddSingletonOverNullDefault<IMultiStreamCommandStore, PgMultiStreamCommandStore>();
    // The worker requires the schema gate, and this registration deliberately does not add one: the worker pipeline
    // registers the gate its initializer marks ready. A gate added here would never be marked ready in a host without
    // that initializer, and every other worker that waits on a registered gate would wait on it forever.
    services.TryAddSingleton<MultiStreamCommandWorker>();
    services.AddHostedService(sp => sp.GetRequiredService<MultiStreamCommandWorker>());
    services.AddHostedService<MultiStreamCommandSubscriber>();
    return services;
  }
}

/// <summary>Subscribes the worker to new-run notices on the instance's shared listen connection.</summary>
/// <docs>fundamentals/dispatcher/multi-stream-commands#worker</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandPostgresRegistrationTests.cs:Subscriber_SubscribesTheWorkerToNewRunNoticesUntilStoppedAsync</tests>
internal sealed class MultiStreamCommandSubscriber(ISharedNotifyConnection connection, MultiStreamCommandWorker worker) : IHostedService {
  private IDisposable? _subscription;

  public Task StartAsync(CancellationToken cancellationToken) {
    _subscription = connection.Subscribe(worker);
    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken) {
    _subscription?.Dispose();
    _subscription = null;
    return Task.CompletedTask;
  }
}

/// <summary>AOT-safe binding of <see cref="MultiStreamCommandOptions"/> from <see cref="MultiStreamCommandOptions.SECTION"/>.
/// A value that does not parse keeps its default.</summary>
/// <docs>fundamentals/dispatcher/multi-stream-commands#configuration</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandPostgresRegistrationTests.cs:Options_BindFromTheirSectionAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandPostgresRegistrationTests.cs:Options_AValueThatDoesNotParse_KeepsItsDefaultAsync</tests>
internal sealed class ConfigureMultiStreamCommandOptionsFromConfiguration(IConfiguration configuration)
  : IConfigureOptions<MultiStreamCommandOptions> {

  public void Configure(MultiStreamCommandOptions options) {
    var section = configuration.GetSection(MultiStreamCommandOptions.SECTION);
    if (bool.TryParse(section[nameof(options.Enabled)], out var enabled)) {
      options.Enabled = enabled;
    }
    if (int.TryParse(section[nameof(options.BatchSize)], NumberStyles.Integer, CultureInfo.InvariantCulture, out var batch)) {
      options.BatchSize = batch;
    }
    if (TimeSpan.TryParse(section[nameof(options.Lease)], CultureInfo.InvariantCulture, out var lease)) {
      options.Lease = lease;
    }
    if (int.TryParse(section[nameof(options.MaxAttempts)], NumberStyles.Integer, CultureInfo.InvariantCulture, out var attempts)) {
      options.MaxAttempts = attempts;
    }
    if (TimeSpan.TryParse(section[nameof(options.NoHandlerRetryDelay)], CultureInfo.InvariantCulture, out var noHandler)) {
      options.NoHandlerRetryDelay = noHandler;
    }
    if (TimeSpan.TryParse(section[nameof(options.PollInterval)], CultureInfo.InvariantCulture, out var poll)) {
      options.PollInterval = poll;
    }
    if (TimeSpan.TryParse(section[nameof(options.CompletedRunRetention)], CultureInfo.InvariantCulture, out var retention)) {
      options.CompletedRunRetention = retention;
    }
    if (TimeSpan.TryParse(section[nameof(options.PurgeInterval)], CultureInfo.InvariantCulture, out var purge)) {
      options.PurgeInterval = purge;
    }
  }
}
