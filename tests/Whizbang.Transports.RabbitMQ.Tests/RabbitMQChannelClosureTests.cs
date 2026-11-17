// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// <see cref="RabbitMQChannelClosure.IsChannelGone"/> is the one test every ack, nack and consume path
/// tolerates a vanished channel by: a closed channel and a disposed one are both gone, and nothing else is.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.RabbitMQ/RabbitMQChannelClosure.cs</code-under-test>
public class RabbitMQChannelClosureTests {
  [Test]
  public async Task IsChannelGone_AClosedChannel_IsGoneAsync() {
    var closed = new AlreadyClosedException(new ShutdownEventArgs(ShutdownInitiator.Peer, replyCode: 320, replyText: "CONNECTION_FORCED"));

    await Assert.That(RabbitMQChannelClosure.IsChannelGone(closed)).IsTrue();
  }

  [Test]
  public async Task IsChannelGone_ADisposedChannel_IsGoneAsync() {
    await Assert.That(RabbitMQChannelClosure.IsChannelGone(new ObjectDisposedException("channel"))).IsTrue();
  }

  [Test]
  public async Task IsChannelGone_AnyOtherFailure_IsNotGoneAsync() {
    await Assert.That(RabbitMQChannelClosure.IsChannelGone(new InvalidOperationException("broker refused"))).IsFalse()
      .Because("only a vanished channel is left to the broker's redelivery; any other failure takes its own path");
  }
}
