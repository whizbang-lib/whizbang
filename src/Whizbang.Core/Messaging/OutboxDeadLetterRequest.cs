// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// Why outbox messages that were never stored are being dead-lettered, and by whom: the audit columns of the
/// dead letters <see cref="IWorkCoordinator.StoreOutboxDeadLettersAsync"/> writes (#1871).
/// </summary>
/// <param name="FailureReason">The reason the dead letters carry, which picks their recovery policy.</param>
/// <param name="ErrorText">The failure, as the dead letters keep it; null when there was none.</param>
/// <param name="Attempts">How many times the store was attempted.</param>
/// <param name="InstanceId">The instance recording the dead letters.</param>
/// <param name="Generation">The current generation tag, read by the recovery worker's generation replay.</param>
/// <docs>extending/internals/outbox-batch-strategy#when-a-flush-fails</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxFlushPoolExhaustionTests.cs</tests>
public sealed record OutboxDeadLetterRequest(
    MessageFailureReason FailureReason,
    string? ErrorText,
    int Attempts,
    Guid InstanceId,
    string Generation);
