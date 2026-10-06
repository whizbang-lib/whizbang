// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

// The classifier matches RabbitMQ exceptions by namespace prefix, so the fake below has to live in
// the production namespace. Block-scoped namespaces for that reason, as in TransportFailureClassifierFakes.

namespace RabbitMQ.Client {
  /// <summary>A RabbitMQ-namespaced exception whose message is null, as an override is free to return.</summary>
  public sealed class NullMessageBrokerException : System.Exception {
    public NullMessageBrokerException() { }
    public NullMessageBrokerException(string message) : base(message) { }
    public NullMessageBrokerException(string message, System.Exception inner) : base(message, inner) { }
    public override string Message => null!;
  }
}

namespace Whizbang.Core.Tests.Workers {
  /// <summary>
  /// <see cref="TransportFailureClassifier"/> reading a broker exception whose message is null.
  /// </summary>
  /// <code-under-test>src/Whizbang.Core/Workers/TransportFailureClassifier.cs</code-under-test>
  [Category("Workers")]
  public class TransportFailureClassifierBranchCoverageTests {

    [Test]
    public async Task Classify_RabbitMqExceptionWithNullMessage_IsATransportFailureNotThrottlingAsync() {
      var reason = TransportFailureClassifier.Classify(new RabbitMQ.Client.NullMessageBrokerException());

      await Assert.That(reason).IsEqualTo(MessageFailureReason.TransportException)
        .Because("with no message there is no flow-control signal to read, so it is a plain transport failure, and reading it must not throw");
    }
  }
}
