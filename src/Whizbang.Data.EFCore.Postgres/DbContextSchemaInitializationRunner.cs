// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The EF Core driver's <see cref="ISchemaInitializationRunner"/>: delegates to the static
/// <see cref="DbContextInitializationRegistry"/> that every EF Core Postgres DbContext self-registers into.
/// </summary>
/// <docs>data/turnkey-initialization</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ISchemaInitializationRunnerCoverageTests.cs</tests>
internal sealed class DbContextSchemaInitializationRunner(
    IServiceProvider serviceProvider,
    ILogger<DbContextSchemaInitializationRunner> logger) : ISchemaInitializationRunner {
  public Task RunAsync(CancellationToken cancellationToken)
    => DbContextInitializationRegistry.InitializeAllAsync(serviceProvider, logger, cancellationToken);
}
