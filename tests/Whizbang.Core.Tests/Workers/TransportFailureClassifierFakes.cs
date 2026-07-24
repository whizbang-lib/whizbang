// Lightweight fakes for the production transport-exception namespaces. The
// TransportFailureClassifier matches by FullName so the namespace MUST be the production
// one. Keeping these in a brace-scoped namespace lets the test assembly avoid taking
// references on the Azure.Messaging.ServiceBus / RabbitMQ.Client packages while still
// exercising the classifier's name-and-message detection.

namespace Azure.Messaging.ServiceBus {
  public class ServiceBusException : System.Exception {
    public ServiceBusException() { }
    public ServiceBusException(string message) : base(message) { }
    public ServiceBusException(string message, System.Exception inner) : base(message, inner) { }
  }
}

namespace RabbitMQ.Client {
  public class OperationInterruptedException : System.Exception {
    public OperationInterruptedException() { }
    public OperationInterruptedException(string message) : base(message) { }
    public OperationInterruptedException(string message, System.Exception inner) : base(message, inner) { }
  }
}
