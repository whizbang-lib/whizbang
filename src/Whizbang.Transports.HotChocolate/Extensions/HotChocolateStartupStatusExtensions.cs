using HotChocolate;
using HotChocolate.Execution.Configuration;
using HotChocolate.Resolvers;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;

namespace Whizbang.Transports.HotChocolate;

/// <summary>Settings the startup status query field reads — one instance per executor.</summary>
/// <param name="IncludeReasons">
/// Whether per-step <c>reason</c> strings and raw fleet failure text are included. Off by default —
/// reasons originate in exception messages and are a separate opt-in level, not a verbosity dial.
/// </param>
/// <docs>proposals/startup-pipeline#status</docs>
public sealed record WhizbangStartupStatusGraphOptions(bool IncludeReasons);

/// <summary>
/// The <c>whizbangStartup</c> query field: the same two-section <see cref="StartupStatusReport"/>
/// every surface serves, contributed as a type extension so <c>[Authorize]</c> and the existing
/// Whizbang GraphQL security integration apply to it exactly as to any other field.
/// </summary>
/// <remarks>
/// A GraphQL schema whose build touches Whizbang lens types may not be buildable during
/// <c>Migrate</c> at all — which is why the minimal-API surface is the primary one and this field
/// is a convenience. A diagnostic reachable only through the subsystem under diagnosis is not a
/// diagnostic.
/// </remarks>
/// <docs>proposals/startup-pipeline#status</docs>
/// <tests>tests/Whizbang.Transports.HotChocolate.Tests/Unit/StartupStatusQueryTests.cs</tests>
[ExtendObjectType(OperationTypeNames.Query)]
public sealed class WhizbangStartupQueries {
  /// <summary>The startup status of this instance plus the fleet, as the status surface projects it.</summary>
#pragma warning disable CA1822 // HotChocolate binds resolvers on instance members of the type extension
  public Task<StartupStatusReport> GetWhizbangStartupAsync(IResolverContext context, CancellationToken cancellationToken) {
    var services = context.Services;
    var options = services.GetService<WhizbangStartupStatusGraphOptions>();
    return StartupStatusReporter.BuildAsync(
      services.GetService<IStartupPipelineState>(),
      services.GetService<IStartupReadySignal>(),
      services.GetService<IServiceInstanceProvider>(),
      services.GetService<IStartupFleetStatusSource>(),
      options?.IncludeReasons ?? false,
      cancellationToken);
  }
#pragma warning restore CA1822
}

/// <summary>Registers the <c>whizbangStartup</c> query field. Opt-in — one explicit call.</summary>
/// <docs>proposals/startup-pipeline#status</docs>
public static class HotChocolateStartupStatusExtensions {
  /// <summary>
  /// Adds the <c>whizbangStartup</c> query field to the schema. Publishing internal state is the
  /// host's decision, so nothing contributes this field implicitly.
  /// </summary>
  /// <param name="builder">The HotChocolate request executor builder.</param>
  /// <param name="includeReasons">Whether to include per-step <c>reason</c> strings and raw fleet
  /// failure text. Off by default — reasons carry content the framework does not control.</param>
  public static IRequestExecutorBuilder AddWhizbangStartupStatus(
      this IRequestExecutorBuilder builder,
      bool includeReasons = false) {
    ArgumentNullException.ThrowIfNull(builder);
    builder.Services.AddSingleton(new WhizbangStartupStatusGraphOptions(includeReasons));
    return builder.AddTypeExtension<WhizbangStartupQueries>();
  }
}
