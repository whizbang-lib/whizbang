// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;

namespace Whizbang.Core.Archival;

/// <summary>
/// The event store's archive seam (#1738, WS-3 S9).
/// </summary>
/// <param name="inner">The driver's event store.</param>
/// <param name="archive">The registered archive store.</param>
/// <param name="readThrough">Whether reads from the start read through the archive.</param>
/// <docs>fundamentals/events/archive-store#read-through</docs>
/// <tests>tests/Whizbang.Core.Tests/Archival/ArchiveAwareEventStoreDecoratorTests.cs</tests>
public sealed class ArchiveAwareEventStoreDecorator(IEventStore inner, IArchiveStore archive, bool readThrough)
    : ForwardingEventStoreDecorator(inner) {
  /// <summary>The version every prefix archive removes.</summary>
  public const long FIRST_ARCHIVED_VERSION = 1;

  /// <summary>Whether reads from the start read through the archive.</summary>
  public bool ReadThrough { get; } = archive is null ? throw new ArgumentNullException(nameof(archive)) : readThrough;
}
