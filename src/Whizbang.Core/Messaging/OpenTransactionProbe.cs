// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Signals;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Whether a scope holds a database transaction open, for the batch buffer (#2220): a publish inside one goes past a
/// full buffer instead of waiting, because it holds a connection the flushes that would make room may need.
/// </summary>
/// <remarks>
/// Two signals, neither of them reflection: an ambient <see cref="System.Transactions.Transaction"/>
/// (<c>TransactionScope</c>), and the transaction a registered <see cref="IAppSignalTransactionSource"/> reports for the
/// scope. A data driver registers one of those for every framework database context, reporting the context's current
/// transaction, so a handler that opened a transaction on it is seen with no application wiring. A transaction opened on
/// a raw connection the framework does not know of is not seen.
/// </remarks>
/// <docs>messaging/outbox-pattern#bounded-buffer</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/StreamAffinityBatchBufferTests.cs</tests>
internal static class OpenTransactionProbe {
  /// <summary>Whether <paramref name="scope"/> holds a database transaction open.</summary>
  /// <param name="scope">The publisher's scope.</param>
  /// <returns><see langword="true"/> when an ambient transaction or a scope transaction source reports one.</returns>
  public static bool HoldsOpenTransaction(IServiceProvider scope) {
    return System.Transactions.Transaction.Current is not null
      || scope.GetServices<IAppSignalTransactionSource>().Any(static source => source.CurrentTransaction is not null);
  }
}
