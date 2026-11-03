// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Whizbang.Sagas;

/// <summary>
/// Reports, when its host starts, that the host's <c>AddWhizbangSagas</c> asked for a per-item stream namespace other
/// than the process-wide one an earlier registration set, which is kept (#2021). Registered only for such a host.
/// </summary>
/// <remarks>
/// The host's own saga services are unaffected: they derive under the host's namespace through
/// <see cref="ISagaItemStreamIdDeriver"/>. What the warning names is the difference for code that derives with no
/// container, through <see cref="SagaItemStreams.Of(Guid, string)"/>, which reads the process-wide namespace.
/// </remarks>
/// <docs>fundamentals/sagas/whizbang-sagas#per-item-streams-cross-pod-safe-fan-out</docs>
/// <tests>tests/Whizbang.Sagas.Tests/SagaItemStreamsFirstRegistrationWinsTests.cs:ALaterRegistrationWithADifferentNamespace_WarnsAtStartNamingBothAsync</tests>
internal sealed partial class SagaItemStreamNamespaceConflictWarning(
    ILogger<SagaItemStreamNamespaceConflictWarning> logger, Guid processWide, Guid requested) : IHostedService {

  /// <inheritdoc/>
  public Task StartAsync(CancellationToken cancellationToken) {
    LogNamespaceNotApplied(logger, requested, processWide);
    return Task.CompletedTask;
  }

  /// <inheritdoc/>
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "This host's saga per-item stream namespace {Requested} was not applied process-wide: an earlier "
      + "AddWhizbangSagas registration set {ProcessWide}, and the first registration is kept. This host's saga services "
      + "derive under {Requested}; SagaItemStreams.Of without a namespace derives under {ProcessWide}.")]
  private static partial void LogNamespaceNotApplied(ILogger logger, Guid requested, Guid processWide);
}
