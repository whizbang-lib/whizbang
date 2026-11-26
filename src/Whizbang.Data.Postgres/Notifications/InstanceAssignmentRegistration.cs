// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Health;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Registers, for every Postgres driver through <see cref="PostgresCoordinationRegistration"/>, the monitor that reads
/// and logs this instance's assignment from the Coordinate stage on and the <c>assignment</c> health component that
/// reports it (#2595). Each driver registers the <see cref="IInstanceAssignmentReader"/> the monitor reads.
/// </summary>
/// <docs>operations/startup/startup-status#instance-assignments</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceAssignmentDriverParityTests.cs:BothDrivers_RegisterTheMonitorAndTheAssignmentHealthComponent_ThatReadTheInstancesOwnRowAsync</tests>
internal static class InstanceAssignmentRegistration {
  /// <summary>The configuration section the options bind from.</summary>
  internal const string CONFIGURATION_SECTION = PostgresNotificationsServiceCollectionExtensions.CONFIGURATION_SECTION + ":InstanceAssignment";

  internal static IServiceCollection AddWhizbangInstanceAssignmentMonitor(this IServiceCollection services) {
    services.AddOptions<InstanceAssignmentOptions>();
    services.AddOptions<PartitionAssignerOptions>();
    services.AddOptions<RoleAssignmentOptions>();
    services.TryAddEnumerable(ServiceDescriptor.Singleton<
      IConfigureOptions<InstanceAssignmentOptions>, ConfigureInstanceAssignmentOptionsFromConfiguration>());
    services.TryAddSingleton(sp => new InstanceAssignmentMonitor(
      sp.GetService<IInstanceAssignmentReader>(),
      sp.GetRequiredService<IServiceInstanceProvider>(),
      sp.GetRequiredService<IOptions<InstanceAssignmentOptions>>(),
      sp.GetRequiredService<IOptions<PartitionAssignerOptions>>(),
      sp.GetRequiredService<IOptions<RoleAssignmentOptions>>(),
      sp.GetRequiredService<ILogger<InstanceAssignmentMonitor>>()));
    // The share is published from Coordinate on, by the assigner elected there; reading earlier would only say "starting".
    services.AddHostedServiceAtStage(StartupStage.Coordinate, sp => sp.GetRequiredService<InstanceAssignmentMonitor>());
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IWhizbangHealthSource, InstanceAssignmentHealthSource>(sp =>
      new InstanceAssignmentHealthSource(sp.GetRequiredService<InstanceAssignmentMonitor>())));
    return services;
  }
}

/// <summary>
/// AOT-safe binding of <see cref="InstanceAssignmentOptions"/> from <c>Whizbang:Database:InstanceAssignment</c>:
/// <c>ObserveInterval</c> and <c>AwaitingShareGrace</c>. A key that is absent or does not parse leaves the default.
/// </summary>
/// <docs>operations/configuration/configuration-reference#instance-assignment</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceAssignmentDriverParityTests.cs:TheOptions_BindFromConfiguration_AndKeepTheirDefaultsWithoutItAsync</tests>
internal sealed class ConfigureInstanceAssignmentOptionsFromConfiguration(IConfiguration configuration)
  : IConfigureOptions<InstanceAssignmentOptions> {
  public void Configure(InstanceAssignmentOptions options) {
    var section = configuration.GetSection(InstanceAssignmentRegistration.CONFIGURATION_SECTION);
    if (TimeSpan.TryParse(section[nameof(InstanceAssignmentOptions.ObserveInterval)], System.Globalization.CultureInfo.InvariantCulture, out var interval)) {
      options.ObserveInterval = interval;
    }
    if (TimeSpan.TryParse(section[nameof(InstanceAssignmentOptions.AwaitingShareGrace)], System.Globalization.CultureInfo.InvariantCulture, out var grace)) {
      options.AwaitingShareGrace = grace;
    }
  }
}
