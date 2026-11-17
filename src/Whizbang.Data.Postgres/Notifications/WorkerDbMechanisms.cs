// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

// The mechanism each of this package's workers' database statements are attributed to (see WorkerDbMechanisms.cs in
// Whizbang.Core). The notification connection itself is deliberately absent: it is the liveness connection, and is
// never attributed.

using Whizbang.Core.Observability;

namespace Whizbang.Data.Postgres.Notifications;

public partial class PgInstanceLifecycleMonitor : IDbMechanismOwner {
  /// <inheritdoc />
  public DbMechanism DbMechanism => DbMechanism.Lifecycle;
}

public partial class PgCommitOrderStamperWorker : IDbMechanismOwner {
  /// <inheritdoc />
  public DbMechanism DbMechanism => DbMechanism.CommitOrder;
}

public partial class PgDurableSignalRetentionWorker : IDbMechanismOwner {
  /// <inheritdoc />
  public DbMechanism DbMechanism => DbMechanism.Signals;
}

public partial class PgDurableSignalTailWorker : IDbMechanismOwner {
  /// <inheritdoc />
  public DbMechanism DbMechanism => DbMechanism.Signals;
}
