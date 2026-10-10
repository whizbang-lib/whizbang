// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Postgres;

/// <summary>
/// One driver's share of schema initialization: what the driver migrates at host start. The schema initializer
/// runs every registered runner, in registration order, under its own blocking, timeout and retry options, and
/// opens <see cref="Whizbang.Core.Workers.ISchemaReadyGate"/> only after the last one returns.
/// </summary>
/// <remarks>
/// A driver registers its runner with
/// <c>services.TryAddEnumerable(ServiceDescriptor.Singleton&lt;ISchemaInitializationRunner, TRunner&gt;(factory))</c>
/// and calls <see cref="SchemaInitializationRegistration.AddWhizbangSchemaInitialization"/>. A runner is idempotent:
/// the initializer re-runs it after a failed attempt.
/// </remarks>
/// <docs>data/turnkey-initialization</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Schema/SchemaInitializationRunnersTests.cs</tests>
public interface ISchemaInitializationRunner {
  /// <summary>
  /// Initializes the driver's schema. Idempotent; it decides itself what, if anything, to run.
  /// </summary>
  /// <param name="cancellationToken">Canceled at host shutdown or when the configured migration timeout passes.</param>
  Task RunAsync(CancellationToken cancellationToken);
}
