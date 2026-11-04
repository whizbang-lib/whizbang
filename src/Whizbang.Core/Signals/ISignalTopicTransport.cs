// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Signals;

/// <summary>
/// A transport that also carries application topics: named channels with a text payload, published and subscribed
/// through the bus as <see cref="Whizbang.Core.Notifications.AppSignals.IAppSignalChannel"/> (#2046). The bus validates
/// the topic; the transport owns the wire.
/// </summary>
/// <docs>fundamentals/work-coordinator/app-signals</docs>
public interface ISignalTopicTransport {
  /// <summary>Sends <paramref name="payload"/> to every subscriber of <paramref name="topic"/>, on every instance.</summary>
  Task PublishTopicAsync(string topic, string payload, CancellationToken cancellationToken = default);

  /// <summary>Calls <paramref name="handler"/> with each payload published to <paramref name="topic"/>; dispose to stop.</summary>
  IDisposable SubscribeTopic(string topic, Func<string, CancellationToken, Task> handler);
}
