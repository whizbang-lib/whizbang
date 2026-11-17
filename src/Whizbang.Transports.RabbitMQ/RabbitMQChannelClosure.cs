// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using RabbitMQ.Client.Exceptions;

namespace Whizbang.Transports.RabbitMQ;

/// <summary>
/// The one test for "the channel is gone": RabbitMQ reports a channel closed under an operation as
/// <see cref="AlreadyClosedException"/>, and one disposed under it as <see cref="ObjectDisposedException"/>.
/// Either way the broker still holds the unacknowledged delivery and redelivers it, so every ack, nack and
/// consume path tolerates both the same way.
/// </summary>
/// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMQChannelClosureTests.cs</tests>
internal static class RabbitMQChannelClosure {
  /// <summary>Whether <paramref name="ex"/> says the channel closed or was disposed under the operation.</summary>
  internal static bool IsChannelGone(Exception ex) => ex is AlreadyClosedException or ObjectDisposedException;
}
