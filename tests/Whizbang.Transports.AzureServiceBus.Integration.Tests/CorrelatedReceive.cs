// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Azure.Messaging.ServiceBus;

namespace Whizbang.Transports.AzureServiceBus.Integration.Tests;

/// <summary>
/// Receives from a shared emulator subscription until a test's own message arrives.
/// </summary>
/// <remarks>
/// <para>
/// The emulator entities are shared by many test classes, so the first message a receiver gets is
/// not necessarily the one the test published: a message another test left behind, or one whose
/// lock lapsed after a drain, can arrive first. Asserting on "the first message" then fails on a
/// stranger's message, and a bare "a message arrived" check passes on one.
/// </para>
/// <para>
/// Each receive returns as soon as the broker delivers a message, so the wait is on the broker's
/// delivery, not on the clock. Every other message is completed (so it is not redelivered into a
/// later test) and reported back; the budget only bounds how long a missing message is waited for.
/// </para>
/// </remarks>
internal static class CorrelatedReceive {
  /// <summary>
  /// Receives until the message with <paramref name="messageId"/> arrives, completing it and every
  /// message received before it.
  /// </summary>
  /// <param name="receiver">A peek-lock receiver on the subscription the message was published to.</param>
  /// <param name="messageId">The broker message id of the message the test published.</param>
  /// <param name="budget">How long to wait for the message before failing.</param>
  /// <param name="cancellationToken">The test's cancellation token.</param>
  /// <returns>The matching message, and the ids of every other message received before it.</returns>
  /// <exception cref="TimeoutException">The message did not arrive within <paramref name="budget"/>.</exception>
  public static async Task<(ServiceBusReceivedMessage Message, IReadOnlyList<string> OthersReceived)> UntilAsync(
      ServiceBusReceiver receiver,
      string messageId,
      TimeSpan budget,
      CancellationToken cancellationToken = default) {
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    cts.CancelAfter(budget);
    var others = new List<string>();
    try {
      while (true) {
        // A null result is the broker's own idle answer; ask again until ours arrives.
        var message = await receiver.ReceiveMessageAsync(budget, cts.Token);
        if (message is null) {
          continue;
        }
        await receiver.CompleteMessageAsync(message, CancellationToken.None);
        if (string.Equals(message.MessageId, messageId, StringComparison.Ordinal)) {
          return (message, others);
        }
        others.Add(message.MessageId);
      }
    } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
      throw new TimeoutException(
        $"Message {messageId} did not arrive on {receiver.EntityPath} within {budget}; "
        + $"received instead: [{string.Join(", ", others)}]");
    }
  }
}
