// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

// Lightweight fakes for the production transport-exception namespaces. The
// TransportFailureClassifier matches by FullName so the namespace MUST be the production
// one. Keeping these in a brace-scoped namespace lets the test assembly avoid taking
// references on the Azure.Messaging.ServiceBus / RabbitMQ.Client packages while still
// exercising the classifier's name-and-message detection.

namespace Azure.Messaging.ServiceBus {
  public class ServiceBusException : System.Exception {
    private readonly bool _nullMessage;
    public ServiceBusException() { }
    public ServiceBusException(string message) : base(message) { }
    public ServiceBusException(string message, System.Exception inner) : base(message, inner) { }
    private ServiceBusException(bool nullMessage) => _nullMessage = nullMessage;

    /// <summary>One whose message is null, as an override is free to return; the classifier matches by full name, so it has to be this type.</summary>
    public static ServiceBusException WithNullMessage() => new(nullMessage: true);

    public override string Message => _nullMessage ? null! : base.Message;
  }
}

namespace RabbitMQ.Client {
  public class OperationInterruptedException : System.Exception {
    public OperationInterruptedException() { }
    public OperationInterruptedException(string message) : base(message) { }
    public OperationInterruptedException(string message, System.Exception inner) : base(message, inner) { }
  }
}
