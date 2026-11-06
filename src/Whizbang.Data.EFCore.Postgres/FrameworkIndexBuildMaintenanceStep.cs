// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>The maintenance step that builds the framework's concurrent indexes (#2092).</summary>
/// <docs>operations/infrastructure/migrations#indexes-built-after-the-migrations</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ConcurrentIndexBuildTests.cs</tests>
#pragma warning disable CS9113, S1144 // RED stub (#2092)
public sealed class FrameworkIndexBuildMaintenanceStep(
    Type dbContextType,
    ILogger<FrameworkIndexBuildMaintenanceStep>? logger = null) : IMaintenanceStep {
#pragma warning restore CS9113, S1144
  /// <inheritdoc />
  public string Name => "framework-index-build";

  /// <inheritdoc />
  public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) => Task.CompletedTask;
}
