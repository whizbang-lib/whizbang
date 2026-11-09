// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;

namespace Whizbang.Core.Signals;

/// <summary>
/// Reports the transaction a scope has open, for the application signal channel (#2076). A channel resolved in a scope
/// asks each registered source in turn and sends a publish inside the first transaction one reports, so the notification
/// is delivered only when that transaction commits and never when it rolls back.
/// </summary>
/// <remarks>
/// Registered scoped by the data driver for every framework DbContext, so no application wires one. A source reports
/// <see langword="null"/> when its scope has no transaction open; the publish is then sent at once.
/// </remarks>
/// <docs>fundamentals/work-coordinator/app-signals#transactional-publish</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Signals/ScopeBoundAppSignalChannelTests.cs:InAScope_TheFirstSourceReportingATransactionWinsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreAppSignalTransactionSourceTests.cs:Postgres_RegistersTheContextsTransactionSource_WithNoApplicationWiringAsync</tests>
public interface IAppSignalTransactionSource {
  /// <summary>The transaction open in this scope, or <see langword="null"/> when there is none.</summary>
  DbTransaction? CurrentTransaction { get; }
}
