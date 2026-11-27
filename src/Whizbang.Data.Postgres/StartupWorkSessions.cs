// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Opens the <see cref="StartupWorkSession"/> a start's Converge stage runs its heavy work through (#2596), the same way
/// for both Postgres drivers: the ledger over the schema, this instance's id, the cap from configuration, the stage
/// reporter and the host's clock, each from the host's services when it has them.
/// </summary>
/// <docs>operations/startup/startup-pipeline#heavy-startup-work</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupWorkSessionsTests.cs</tests>
public static class StartupWorkSessions {
  /// <summary>The configuration section the cap binds from.</summary>
  internal const string CONFIGURATION_SECTION = PostgresNotificationsServiceCollectionExtensions.CONFIGURATION_SECTION + ":StartupWork";

  /// <summary>Opens a session for one start.</summary>
  /// <param name="connectionFactory">Opens a connection of the ledger's own; null runs every unit directly.</param>
  /// <param name="schema">The schema, bare.</param>
  /// <param name="services">The host's services, for the cap, the stage reporter, the clock and the logger; may be null.</param>
  /// <param name="passApplied">Whether this start's own schema pass applied anything.</param>
  /// <param name="logger">Where the session reports when the host registers no logger factory.</param>
  /// <param name="instanceId">This instance; null takes it from <paramref name="services"/>, and runs every unit directly without one.</param>
  /// <returns>The session; dispose it once the Converge work is done, to give its permit back.</returns>
  /// <exception cref="ArgumentOutOfRangeException">The configured cap cannot work.</exception>
  public static StartupWorkSession Begin(
      Func<NpgsqlConnection>? connectionFactory, string schema, IServiceProvider? services, bool passApplied,
      ILogger? logger = null, Guid? instanceId = null) {
    var options = services?.GetService<IOptions<StartupWorkOptions>>()?.Value ?? new StartupWorkOptions();
    options.Validate();
    var id = instanceId ?? services?.GetService<IServiceInstanceProvider>()?.InstanceId;
    return new StartupWorkSession(
      connectionFactory is null ? null : new PgStartupWorkLedger(connectionFactory, schema), id, options, passApplied,
      services?.GetService<IStartupStageReporter>(), services?.GetService<TimeProvider>() ?? TimeProvider.System,
      services?.GetService<ILogger<StartupWorkSession>>() ?? logger);
  }

  /// <summary>Registers the cap's options, bound from <c>Whizbang:Database:StartupWork</c>, for every Postgres driver.</summary>
  /// <param name="services">The service collection.</param>
  /// <returns>The same collection.</returns>
  internal static IServiceCollection AddWhizbangStartupWorkCap(this IServiceCollection services) {
    services.AddOptions<StartupWorkOptions>();
    services.TryAddEnumerable(ServiceDescriptor.Singleton<
      IConfigureOptions<StartupWorkOptions>, ConfigureStartupWorkOptionsFromConfiguration>());
    return services;
  }
}

/// <summary>
/// AOT-safe binding of <see cref="StartupWorkOptions"/> from <c>Whizbang:Database:StartupWork</c>:
/// <c>MaxConcurrentHeavyStarts</c>, <c>PermitPollInterval</c> and <c>PermitWaitLimit</c>. A key that is absent or does
/// not parse leaves the default.
/// </summary>
/// <docs>operations/configuration/configuration-reference#startup-work</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupParity/StartupWorkParityTests.cs:TheCap_BindsFromConfiguration_AndKeepsItsDefaultsWithoutItAsync</tests>
internal sealed class ConfigureStartupWorkOptionsFromConfiguration(IConfiguration configuration) : IConfigureOptions<StartupWorkOptions> {
  public void Configure(StartupWorkOptions options) {
    var section = configuration.GetSection(StartupWorkSessions.CONFIGURATION_SECTION);
    if (int.TryParse(section[nameof(StartupWorkOptions.MaxConcurrentHeavyStarts)], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cap)) {
      options.MaxConcurrentHeavyStarts = cap;
    }
    if (TimeSpan.TryParse(section[nameof(StartupWorkOptions.PermitPollInterval)], CultureInfo.InvariantCulture, out var interval)) {
      options.PermitPollInterval = interval;
    }
    if (TimeSpan.TryParse(section[nameof(StartupWorkOptions.PermitWaitLimit)], CultureInfo.InvariantCulture, out var limit)) {
      options.PermitWaitLimit = limit;
    }
  }
}

/// <summary>
/// What a build gives a unit of shared startup work, as the text its fingerprint is taken over (#2596): the same on both
/// drivers, so two instances of one build agree, whichever driver they run.
/// </summary>
/// <docs>operations/startup/startup-pipeline#heavy-startup-work</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupWorkSessionsTests.cs</tests>
public static class StartupWorkInputs {
  /// <summary>The registry payloads: each payload's service and JSON, in order.</summary>
  /// <param name="payloads">This build's registry payloads.</param>
  /// <returns>The inputs.</returns>
  public static string Of(IEnumerable<Whizbang.Core.Perspectives.PerspectiveRegistryPayload> payloads) {
    ArgumentNullException.ThrowIfNull(payloads);
    return Of(payloads.Select(p => p.ServiceName + "\n" + p.Json));
  }

  /// <summary>
  /// What a build gives the managed-object reconcile (#2608): every declared object and pin, and the settings it plans
  /// with, each in a fixed order, so two instances of one build and configuration agree whatever order their
  /// contributors declared in, and a changed setting (a mode, a kind kept, a pin, the fleet gate) runs it again.
  /// </summary>
  /// <param name="declared">What this build declares.</param>
  /// <param name="settings">The reconcile settings.</param>
  /// <returns>The inputs.</returns>
  public static string Of(Schema.ManagedSchemaObjectSet declared, Schema.ManagedSchemaSettings settings) {
    ArgumentNullException.ThrowIfNull(declared);
    ArgumentNullException.ThrowIfNull(settings);
    var parts = new List<string> {
      string.Create(CultureInfo.InvariantCulture, $"mode:{settings.Mode}"),
      string.Create(CultureInfo.InvariantCulture, $"fleet-gate:{settings.DropAfterFleetConverged}"),
    };
    parts.AddRange(settings.KeepKinds.Select(k => string.Create(CultureInfo.InvariantCulture, $"keep:{k}")).Order(StringComparer.Ordinal));
    parts.AddRange(settings.Pins.Select(p => "pin-glob:" + p).Order(StringComparer.Ordinal));
    parts.AddRange(declared.Objects
      .Select(o => Of([o.Table, o.Name, o.Kind.ToString(), o.DeclaredBy]))
      .Select(o => "object:" + o).Order(StringComparer.Ordinal));
    parts.AddRange(declared.Pins
      .Select(p => Of([p.Table, p.Name, p.Reason ?? "\0", p.DeclaredBy]))
      .Select(p => "pin:" + p).Order(StringComparer.Ordinal));
    return Of(parts);
  }

  /// <summary>Text parts, in order, each ended so no two lists of parts give the same text.</summary>
  /// <param name="parts">The parts.</param>
  /// <returns>The inputs.</returns>
  public static string Of(IEnumerable<string> parts) {
    ArgumentNullException.ThrowIfNull(parts);
    return string.Concat(parts.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Length}:{p}\n")));
  }
}
