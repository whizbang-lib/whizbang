// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;

namespace Whizbang.Core.Signals;

/// <summary>
/// A transport that also carries application topics: named channels with a text payload, published and subscribed
/// through the bus as <see cref="Whizbang.Core.Notifications.AppSignals.IAppSignalChannel"/> (#2046). The bus validates
/// the topic; the transport owns the wire.
/// </summary>
/// <docs>fundamentals/work-coordinator/app-signals</docs>
public interface ISignalTopicTransport {
  /// <summary>Sends <paramref name="payload"/> to every subscriber of <paramref name="topic"/>, on every instance.</summary>
  /// <param name="topic">The validated topic.</param>
  /// <param name="payload">The text subscribers receive.</param>
  /// <param name="transaction">
  /// The caller's open transaction, when the channel found one in its scope (#2076): a transport that can send inside it
  /// does, so the notification is delivered only on commit. <see langword="null"/> when there is none.
  /// </param>
  /// <param name="cancellationToken">Cancels the send.</param>
  Task PublishTopicAsync(string topic, string payload, DbTransaction? transaction, CancellationToken cancellationToken = default);

  /// <summary>Calls <paramref name="handler"/> with each payload published to <paramref name="topic"/>; dispose to stop.</summary>
  IDisposable SubscribeTopic(string topic, Func<string, CancellationToken, Task> handler);
}
