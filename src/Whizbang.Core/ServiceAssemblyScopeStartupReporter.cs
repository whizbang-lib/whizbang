// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Whizbang.Core;

/// <summary>
/// Names, once when the host starts and at Debug, the service the host anchored on and the generated assemblies whose
/// registrations it included and excluded (#2243), so a missing or foreign registration can be traced to the scope.
/// </summary>
/// <param name="scope">The host's scope.</param>
/// <param name="logger">Where the report goes.</param>
/// <docs>extending/extensibility/replaceable-services#service-scope</docs>
/// <tests>tests/Whizbang.Core.Tests/ServiceScope/ServiceAssemblyScopeStartupReporterTests.cs</tests>
internal sealed partial class ServiceAssemblyScopeStartupReporter(
    ServiceAssemblyScope scope, ILogger<ServiceAssemblyScopeStartupReporter> logger) : IHostedService {
  /// <inheritdoc />
  public Task StartAsync(CancellationToken cancellationToken) {
    if (scope.IsScoped) {
      LogScoped(logger, scope.Anchors, scope.Included, scope.Excluded);
    } else {
      LogUnscoped(logger, scope.UnscopedReason!, scope.Included);
    }
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
    Message = "Generated registrations are scoped to the service anchored on [{Anchors}]: included [{Included}]; excluded as another service's [{Excluded}]")]
  private static partial void LogScoped(
      ILogger logger, IReadOnlyList<string> anchors, IReadOnlyList<string> included, IReadOnlyList<string> excluded);

  [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
    Message = "Generated registrations are not scoped to a service ({Reason}): every assembly is included [{Included}]")]
  private static partial void LogUnscoped(ILogger logger, string reason, IReadOnlyList<string> included);
}
