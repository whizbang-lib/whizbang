using Whizbang.Core.Routing;
using Whizbang.Core.Transports;

namespace ECommerce.RabbitMQ.Integration.Tests.Fixtures;

/// <summary>
/// Test-specific routing strategy for RabbitMQ integration tests.
/// Maps topic names to test-specific exchange names to prevent interference between tests.
/// Each test class gets unique exchanges/queues based on the test class name.
/// </summary>
public class TestRabbitMqRoutingStrategy : ITopicRoutingStrategy {
  private readonly string _testClassName;

  public TestRabbitMqRoutingStrategy(string testClassName) {
    _testClassName = testClassName;
  }

  /// <summary>
  /// Resolves topic name to test-specific exchange name.
  /// Example: "products" → "products-OutboxLifecycleTests"
  /// </summary>
  public string ResolveTopic(Type messageType, string baseTopic, IReadOnlyDictionary<string, object>? context = null) {
    return $"{baseTopic}-{_testClassName}";
  }

  /// <summary>
  /// Generates a unique test ID for cleanup purposes.
  /// </summary>
  public string GenerateTestId(string testName) {
    return $"{_testClassName}-{testName}";
  }
}
