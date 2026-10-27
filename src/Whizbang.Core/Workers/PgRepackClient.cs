// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Processes;

namespace Whizbang.Core.Workers;

/// <summary>
/// The pg_repack client program as a capability, <c>program/pg_repack</c>: a prerequisite of
/// <see cref="TableRewriteStrategy.PgRepack"/> in <see cref="PgRepackMode.InProcess"/> mode only. The extension
/// itself is <c>postgres/pg_repack</c>, required by the strategy in either mode.
/// </summary>
/// <docs>operations/infrastructure/storage-maintenance#pg-repack</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/PgRepackTests.cs</tests>
public static class PgRepackClient {
  /// <summary>The key that selects the run mode.</summary>
  internal const string MODE_KEY = "Whizbang:Workers:Maintenance:TableRewrite:PgRepack:Mode";

  private const string CLIENT_PATH_KEY = "Whizbang:Workers:Maintenance:TableRewrite:PgRepack:ClientPath";
  private const string FEATURE = "Online table rewrite, in process (pg_repack client)";

  /// <summary>The provider's name: programs on this host.</summary>
  public const string PROVIDER = "program";

  /// <summary>The client's capability, <c>program/pg_repack</c>.</summary>
  public static CapabilityId Capability { get; } = new(PROVIDER, "pg_repack");

  /// <summary>What the client is, and the operator's fix when it is missing.</summary>
  public static CapabilityDescriptor Descriptor { get; } = new(
    Capability,
    "the pg_repack client program, run in process for online table rewrites",
    [FEATURE],
    Provisionable: false,
    ["the pg_repack client on this host's PATH or at " + CLIENT_PATH_KEY + ", the same version as the pg_repack extension"],
    new Dictionary<CapabilityState, string> {
      [CapabilityState.Missing] = "install the pg_repack client program, the same version as the pg_repack extension, on "
        + "this host's PATH or set " + CLIENT_PATH_KEY + " to it; or set " + MODE_KEY + " = External and run it as a job",
    });

  /// <summary>The requirement in force while the client runs in process.</summary>
  public static CapabilityRequirement Requirement { get; } =
    new(Capability, FEATURE, MODE_KEY, nameof(PgRepackMode.InProcess), nameof(PgRepackMode.InProcess));

  /// <summary>Whether this host runs the client: pg_repack is the strategy and the mode is in process.</summary>
  /// <param name="options">The maintenance options.</param>
  /// <returns>True when the client is started from this process.</returns>
  public static bool RunsInProcess(MaintenanceWorkerOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    return options.TableRewriteStrategy == TableRewriteStrategy.PgRepack
      && options.TableRewrite.PgRepack.Mode == PgRepackMode.InProcess;
  }
}

/// <summary>
/// Probes the pg_repack client by running it with <c>--version</c>, only on a host that runs it in process: a host
/// with another strategy, or in <see cref="PgRepackMode.External"/> mode, has no scope and starts nothing.
/// </summary>
/// <remarks>
/// The scope is the configured <see cref="PgRepackOptions.ClientPath"/>. A client that cannot be started, or that
/// exits with an error, is <see cref="CapabilityState.Missing"/> with the reason; one that answers is
/// <see cref="CapabilityState.Ready"/> with the version it printed. Whizbang never installs a program, so the client
/// is never provisionable.
/// </remarks>
/// <param name="options">The maintenance options: the strategy, the mode and the client path.</param>
/// <param name="runner">Starts the client.</param>
/// <docs>operations/infrastructure/storage-maintenance#pg-repack</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/PgRepackTests.cs</tests>
public sealed class PgRepackClientCapabilityProvider(IOptions<MaintenanceWorkerOptions> options, IExternalProcessRunner runner)
  : ICapabilityProvider {
  /// <inheritdoc />
  public string Name => PgRepackClient.PROVIDER;

  /// <inheritdoc />
  public IReadOnlyList<CapabilityDescriptor> Descriptors { get; } = [PgRepackClient.Descriptor];

  /// <inheritdoc />
  public IReadOnlyList<string> Scopes =>
    PgRepackClient.RunsInProcess(options.Value) ? [options.Value.TableRewrite.PgRepack.ClientPath] : [];

  /// <inheritdoc />
  public async Task<IReadOnlyList<CapabilityStatus>> ProbeAsync(string scope, CancellationToken cancellationToken) {
    var result = await runner.RunAsync(new ExternalProcessRequest(scope, ["--version"]), cancellationToken).ConfigureAwait(false);
    if (!result.Started) {
      return [_status(scope, CapabilityState.Missing, version: null, result.StandardError)];
    }
    if (result.ExitCode != 0) {
      return [_status(scope, CapabilityState.Missing, version: null,
        $"{scope} --version exited {result.ExitCode}: {result.StandardError.Trim()}")];
    }
    return [_status(scope, CapabilityState.Ready, _version(result.StandardOutput), reason: null)];
  }

  private static CapabilityStatus _status(string scope, CapabilityState state, string? version, string? reason) =>
    new(PgRepackClient.Descriptor, scope, state, version, Detail: null, reason);

  // "pg_repack 1.5.2": the last word of the first line.
  private static string? _version(string output) {
    var line = output.Trim().Split('\n')[0].Trim();
    return line.Length == 0 ? null : line.Split(' ')[^1];
  }
}

/// <summary>
/// Puts the pg_repack client's requirement in force while it runs in process: explicit when
/// <c>Whizbang:Workers:Maintenance:TableRewrite:PgRepack:Mode</c> is set, so a host without the client fails start;
/// implicit when the mode is left at its default, so a host without it degrades with a warning and the tables stay
/// queued for an external job.
/// </summary>
/// <param name="options">The maintenance options: the strategy and the mode in force.</param>
/// <docs>operations/infrastructure/storage-maintenance#pg-repack</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/PgRepackTests.cs</tests>
public sealed class PgRepackClientRequirementSource(IOptions<MaintenanceWorkerOptions> options) : ICapabilityRequirementSource {
  /// <inheritdoc />
  public IReadOnlyList<ActiveCapabilityRequirement> Resolve(IConfiguration? configuration) {
    if (!PgRepackClient.RunsInProcess(options.Value)) {
      return [];
    }
    var activation = string.IsNullOrEmpty(configuration?[PgRepackClient.MODE_KEY])
      ? CapabilityActivation.Implicit
      : CapabilityActivation.Explicit;
    return [new ActiveCapabilityRequirement(PgRepackClient.Requirement, PgRepackClient.MODE_KEY, nameof(PgRepackMode.InProcess), activation)];
  }
}
