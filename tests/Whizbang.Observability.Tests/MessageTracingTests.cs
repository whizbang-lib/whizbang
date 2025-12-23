using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Observability.Tests;

/// <summary>
/// Tests for MessageEnvelope, MessageHop, and MessageTracing with caller information capture.
/// These components enable time-travel debugging and VSCode extension support.
/// </summary>
public class MessageTracingTests {
  // Test message types
  private sealed record TestMessage(string Value);
  private sealed record CreateOrder(Guid OrderId, string ProductName);

  #region MessageEnvelope Tests

  [Test]
  [RequiresUnreferencedCode("")]
  [RequiresDynamicCode("")]
  public async Task MessageEnvelope_Constructor_SetsAllPropertiesAsync() {
    // Arrange
    var messageId = MessageId.New();
    var correlationId = CorrelationId.New();
    var causationId = MessageId.New();
    var timestamp = DateTimeOffset.UtcNow;
    var payload = new TestMessage("test");
    var metadata = new Dictionary<string, JsonElement> { ["key"] = JsonSerializer.SerializeToElement("value") };

    var firstHop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Timestamp = timestamp,
      Metadata = metadata,
      CorrelationId = correlationId,
      CausationId = causationId
    };

    // Act
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = messageId,
      Payload = payload,
      Hops = [firstHop]
    };

    // Assert
    await Assert.That(envelope.MessageId).IsEqualTo(messageId);
    await Assert.That(envelope.GetCorrelationId()).IsEqualTo(correlationId);
    await Assert.That(envelope.GetCausationId()).IsEqualTo(causationId);
    await Assert.That(envelope.Payload).IsEqualTo(payload);
    await Assert.That(envelope.GetMessageTimestamp()).IsEqualTo(timestamp);
    var allMetadata = envelope.GetAllMetadata();
    await Assert.That(allMetadata).Count().IsEqualTo(metadata.Count);
    foreach (var kvp in metadata) {
      await Assert.That(allMetadata.ContainsKey(kvp.Key)).IsTrue();
      await Assert.That(allMetadata[kvp.Key]).IsEqualTo(kvp.Value);
    }
  }

  [Test]
  public async Task MessageEnvelope_GetAllPolicyDecisions_ReturnsEmpty_WhenNoHopsHaveTrailsAsync() {
    // Arrange & Act
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [new MessageHop {
        ServiceInstance = new ServiceInstanceInfo {
          ServiceName = "Test",
          InstanceId = Guid.NewGuid(),
          HostName = "test-host",
          ProcessId = 12345
        }
      }]
    };

    // Assert
    var decisions = envelope.GetAllPolicyDecisions();
    await Assert.That(decisions).IsEmpty();
  }

  [Test]
  public async Task MessageEnvelope_RequiresAtLeastOneHopAsync() {
    // Arrange
    var firstHop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "OriginService",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Timestamp = DateTimeOffset.UtcNow
    };

    // Act
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [firstHop]
    };

    // Assert
    await Assert.That(envelope.Hops).Count().IsEqualTo(1);
    await Assert.That(envelope.Hops[0]).IsEqualTo(firstHop);
  }

  [Test]
  public async Task MessageEnvelope_AddHop_AddsHopToListAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [new MessageHop {
        ServiceInstance = new ServiceInstanceInfo {
          ServiceName = "Origin",
          InstanceId = Guid.NewGuid(),
          HostName = "test-host",
          ProcessId = 12345
        }
      }]
    };

    var hop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "test-machine",
        ProcessId = 12345
      },
      Timestamp = DateTimeOffset.UtcNow,
      Topic = "test-topic",
      StreamKey = "test-stream",
      ExecutionStrategy = "SerialExecutor"
    };

    // Act
    envelope.AddHop(hop);

    // Assert
    await Assert.That(envelope.Hops).Count().IsEqualTo(2);
    await Assert.That(envelope.Hops[1]).IsEqualTo(hop);
  }

  [Test]
  public async Task MessageEnvelope_AddHop_MaintainsOrderedListAsync() {
    // Arrange
    var hop0 = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Origin",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Timestamp = DateTimeOffset.UtcNow
    };

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [hop0]
    };

    var hop1 = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Service1",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Timestamp = DateTimeOffset.UtcNow
    };

    await Task.Delay(10); // Ensure different timestamps

    var hop2 = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Service2",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Timestamp = DateTimeOffset.UtcNow
    };

    // Act
    envelope.AddHop(hop1);
    envelope.AddHop(hop2);

    // Assert
    await Assert.That(envelope.Hops).Count().IsEqualTo(3);
    await Assert.That(envelope.Hops[0].ServiceInstance.ServiceName).IsEqualTo("Origin");
    await Assert.That(envelope.Hops[1].ServiceInstance.ServiceName).IsEqualTo("Service1");
    await Assert.That(envelope.Hops[2].ServiceInstance.ServiceName).IsEqualTo("Service2");
  }

  [Test]
  public async Task MessageEnvelope_GetCurrentTopic_ReturnsNull_WhenNoHopsHaveTopicAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "Origin",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Topic = ""
        } // No topic
      ]
    };

    // Act
    var topic = envelope.GetCurrentTopic();

    // Assert
    await Assert.That(topic).IsNull();
  }

  [Test]
  public async Task MessageEnvelope_GetCurrentTopic_ReturnsMostRecentNonNullTopicAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [new MessageHop {
        ServiceInstance = new ServiceInstanceInfo {
          ServiceName = "Test",
          InstanceId = Guid.NewGuid(),
          HostName = "test-host",
          ProcessId = 12345
        }
      }]
    };

    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Service1",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Topic = "orders"
    });

    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Service2",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Topic = "inventory"
    });

    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Service3",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Topic = "" // Empty should be skipped
    });

    // Act
    var topic = envelope.GetCurrentTopic();

    // Assert - Should return "inventory" (last non-empty)
    await Assert.That(topic).IsEqualTo("inventory");
  }

  [Test]
  public async Task MessageEnvelope_GetCurrentStreamKey_ReturnsNull_WhenNoHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [new MessageHop {
        ServiceInstance = new ServiceInstanceInfo {
          ServiceName = "Test",
          InstanceId = Guid.NewGuid(),
          HostName = "test-host",
          ProcessId = 12345
        }
      }]
    };

    // Act
    var streamKey = envelope.GetCurrentStreamKey();

    // Assert
    await Assert.That(streamKey).IsNull();
  }

  [Test]
  public async Task MessageEnvelope_GetCurrentStreamKey_ReturnsMostRecentNonNullStreamKeyAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [new MessageHop {
        ServiceInstance = new ServiceInstanceInfo {
          ServiceName = "Test",
          InstanceId = Guid.NewGuid(),
          HostName = "test-host",
          ProcessId = 12345
        }
      }]
    };

    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Service1",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      StreamKey = "stream-1"
    });

    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Service2",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      StreamKey = "stream-2"
    });

    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Service3",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      StreamKey = "" // Empty should be skipped
    });

    // Act
    var streamKey = envelope.GetCurrentStreamKey();

    // Assert
    await Assert.That(streamKey).IsEqualTo("stream-2");
  }

  [Test]
  public async Task MessageEnvelope_GetCurrentPartitionIndex_ReturnsNull_WhenNoHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [new MessageHop {
        ServiceInstance = new ServiceInstanceInfo {
          ServiceName = "Test",
          InstanceId = Guid.NewGuid(),
          HostName = "test-host",
          ProcessId = 12345
        }
      }]
    };

    // Act
    var partitionIndex = envelope.GetCurrentPartitionIndex();

    // Assert
    await Assert.That(partitionIndex).IsNull();
  }

  [Test]
  public async Task MessageEnvelope_GetCurrentPartitionIndex_ReturnsMostRecentNonNullValueAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [new MessageHop {
        ServiceInstance = new ServiceInstanceInfo {
          ServiceName = "Test",
          InstanceId = Guid.NewGuid(),
          HostName = "test-host",
          ProcessId = 12345
        }
      }]
    };

    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Service1",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      PartitionIndex = 0
    });

    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Service2",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      PartitionIndex = 3
    });

    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Service3",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      PartitionIndex = null // Null should be skipped
    });

    // Act
    var partitionIndex = envelope.GetCurrentPartitionIndex();

    // Assert
    await Assert.That(partitionIndex).IsEqualTo(3);
  }

  [Test]
  public async Task MessageEnvelope_GetCurrentSequenceNumber_ReturnsNull_WhenNoHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [new MessageHop {
        ServiceInstance = new ServiceInstanceInfo {
          ServiceName = "Test",
          InstanceId = Guid.NewGuid(),
          HostName = "test-host",
          ProcessId = 12345
        }
      }]
    };

    // Act
    var sequenceNumber = envelope.GetCurrentSequenceNumber();

    // Assert
    await Assert.That(sequenceNumber).IsNull();
  }

  [Test]
  public async Task MessageEnvelope_GetCurrentSequenceNumber_ReturnsMostRecentNonNullValueAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [new MessageHop {
        ServiceInstance = new ServiceInstanceInfo {
          ServiceName = "Test",
          InstanceId = Guid.NewGuid(),
          HostName = "test-host",
          ProcessId = 12345
        }
      }]
    };

    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Service1",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      SequenceNumber = 100
    });

    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Service2",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      SequenceNumber = 200
    });

    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Service3",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      SequenceNumber = null // Null should be skipped
    });

    // Act
    var sequenceNumber = envelope.GetCurrentSequenceNumber();

    // Assert
    await Assert.That(sequenceNumber).IsEqualTo(200);
  }

  [Test]
  public async Task MessageEnvelope_GetCurrentSecurityContext_ReturnsNull_WhenNoHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [new MessageHop {
        ServiceInstance = new ServiceInstanceInfo {
          ServiceName = "Test",
          InstanceId = Guid.NewGuid(),
          HostName = "test-host",
          ProcessId = 12345
        }
      }]
    };

    // Act
    var securityContext = envelope.GetCurrentSecurityContext();

    // Assert
    await Assert.That(securityContext).IsNull();
  }

  [Test]
  public async Task MessageEnvelope_GetCurrentSecurityContext_ReturnsMostRecentNonNullValueAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [new MessageHop {
        ServiceInstance = new ServiceInstanceInfo {
          ServiceName = "Test",
          InstanceId = Guid.NewGuid(),
          HostName = "test-host",
          ProcessId = 12345
        }
      }]
    };

    var context1 = new SecurityContext {
      UserId = "user-1",
      TenantId = "tenant-a"
    };

    var context2 = new SecurityContext {
      UserId = "user-2",
      TenantId = "tenant-b"
    };

    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Service1",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      SecurityContext = context1
    });

    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Service2",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      SecurityContext = context2
    });

    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Service3",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      SecurityContext = null // Null should be skipped
    });

    // Act
    var securityContext = envelope.GetCurrentSecurityContext();

    // Assert
    await Assert.That(securityContext).IsEqualTo(context2);
    await Assert.That(securityContext!.UserId).IsEqualTo("user-2");
    await Assert.That(securityContext!.TenantId).IsEqualTo("tenant-b");
  }

  [Test]
  public async Task MessageEnvelope_GetMessageTimestamp_ReturnsFirstHopTimestampAsync() {
    // Arrange
    var timestamp = DateTimeOffset.UtcNow;
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "Origin",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Timestamp = timestamp
        }
      ]
    };

    // Act
    var result = envelope.GetMessageTimestamp();

    // Assert
    await Assert.That(result).IsEqualTo(timestamp);
  }

  [Test]
  [RequiresUnreferencedCode("")]
  [RequiresDynamicCode("")]
  public async Task MessageEnvelope_GetMetadata_ReturnsNull_WhenKeyNotFoundAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "Origin",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Metadata = new Dictionary<string, JsonElement> { ["key1"] = JsonSerializer.SerializeToElement("value1") }
        }
      ]
    };

    // Act
    var result = envelope.GetMetadata("nonexistent");

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  [RequiresUnreferencedCode("")]
  [RequiresDynamicCode("")]
  public async Task MessageEnvelope_GetMetadata_ReturnsLatestValue_WhenKeyExistsInMultipleHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "Hop1",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Metadata = new Dictionary<string, JsonElement> { ["priority"] = JsonSerializer.SerializeToElement(5) }
        }
      ]
    };

    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Hop2",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Metadata = new Dictionary<string, JsonElement> { ["priority"] = JsonSerializer.SerializeToElement(10) }
    });

    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Hop3",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Metadata = null // No metadata, should use previous
    });

    // Act
    var result = envelope.GetMetadata("priority");

    // Assert
    await Assert.That(result!.Value.GetInt32()).IsEqualTo(10);
  }

  [Test]
  [RequiresUnreferencedCode("")]
  [RequiresDynamicCode("")]
  public async Task MessageEnvelope_GetAllMetadata_StitchesAllMetadataAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "Hop1",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Metadata = new Dictionary<string, JsonElement> {
            ["priority"] = JsonSerializer.SerializeToElement(5),
            ["tenant"] = JsonSerializer.SerializeToElement("acme")
          }
        }
      ]
    };

    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Hop2",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Metadata = new Dictionary<string, JsonElement> {
        ["priority"] = JsonSerializer.SerializeToElement(10), // Override
        ["enriched"] = JsonSerializer.SerializeToElement(true) // New key
      }
    });

    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Hop3",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Metadata = null // Skip this hop
    });

    // Act
    var result = envelope.GetAllMetadata();

    // Assert
    await Assert.That(result).Count().IsEqualTo(3);
    await Assert.That(result["priority"].GetInt32()).IsEqualTo(10); // Later hop wins
    await Assert.That(result["tenant"].GetString()).IsEqualTo("acme"); // From first hop
    await Assert.That(result["enriched"].GetBoolean()).IsEqualTo(true); // From second hop
  }

  [Test]
  public async Task MessageEnvelope_GetAllMetadata_ReturnsEmpty_WhenNoHopsHaveMetadataAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "Origin",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Metadata = null
        }
      ]
    };

    // Act
    var result = envelope.GetAllMetadata();

    // Assert
    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task MessageEnvelope_GetAllPolicyDecisions_ReturnsSingleHopDecisionsAsync() {
    // Arrange
    var trail = new PolicyDecisionTrail();
    trail.RecordDecision("StreamSelection", "Order.* → order-{id}", true, "order-123", "Matched order pattern");

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "Origin",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Trail = trail
        }
      ]
    };

    // Act
    var decisions = envelope.GetAllPolicyDecisions();

    // Assert
    await Assert.That(decisions).Count().IsEqualTo(1);
    await Assert.That(decisions[0].PolicyName).IsEqualTo("StreamSelection");
    await Assert.That(decisions[0].Matched).IsTrue();
  }

  [Test]
  public async Task MessageEnvelope_GetAllPolicyDecisions_StitchesDecisionsAcrossMultipleHopsAsync() {
    // Arrange
    var trail1 = new PolicyDecisionTrail();
    trail1.RecordDecision("StreamSelection", "Order.* → order-{id}", true, "order-123", "Matched order pattern");

    var trail2 = new PolicyDecisionTrail();
    trail2.RecordDecision("ExecutionStrategy", "order-* → SerialExecutor", true, "SerialExecutor", "Orders must be processed serially");

    var trail3 = new PolicyDecisionTrail();
    trail3.RecordDecision("PartitionStrategy", "hash(order-123) → partition-5", true, 5, "Hashed to partition 5");

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [
        new MessageHop {

          ServiceInstance = new ServiceInstanceInfo {

            ServiceName = "Origin",

            InstanceId = Guid.NewGuid(),

            HostName = "test-host",

            ProcessId = 12345

          }, Trail = trail1 }
      ]
    };

    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Router",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Trail = trail2
    });
    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Executor",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Trail = trail3
    });

    // Act
    var decisions = envelope.GetAllPolicyDecisions();

    // Assert
    await Assert.That(decisions).Count().IsEqualTo(3);
    await Assert.That(decisions[0].PolicyName).IsEqualTo("StreamSelection");
    await Assert.That(decisions[1].PolicyName).IsEqualTo("ExecutionStrategy");
    await Assert.That(decisions[2].PolicyName).IsEqualTo("PartitionStrategy");
  }

  [Test]
  public async Task MessageEnvelope_GetAllPolicyDecisions_MaintainsChronologicalOrderAsync() {
    // Arrange
    var trail1 = new PolicyDecisionTrail();
    trail1.RecordDecision("Policy1", "Rule1", true, null, "First decision");
    await Task.Delay(10); // Ensure different timestamps
    trail1.RecordDecision("Policy2", "Rule2", true, null, "Second decision");

    var trail2 = new PolicyDecisionTrail();
    await Task.Delay(10);
    trail2.RecordDecision("Policy3", "Rule3", true, null, "Third decision");

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [
        new MessageHop {

          ServiceInstance = new ServiceInstanceInfo {

            ServiceName = "Hop1",

            InstanceId = Guid.NewGuid(),

            HostName = "test-host",

            ProcessId = 12345

          }, Trail = trail1 }
      ]
    };

    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Hop2",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Trail = trail2
    });

    // Act
    var decisions = envelope.GetAllPolicyDecisions();

    // Assert
    await Assert.That(decisions).Count().IsEqualTo(3);
    await Assert.That(decisions[0].Reason).IsEqualTo("First decision");
    await Assert.That(decisions[1].Reason).IsEqualTo("Second decision");
    await Assert.That(decisions[2].Reason).IsEqualTo("Third decision");

    // Verify timestamps are in order
    await Assert.That(decisions[0].Timestamp).IsLessThan(decisions[1].Timestamp);
    await Assert.That(decisions[1].Timestamp).IsLessThan(decisions[2].Timestamp);
  }

  [Test]
  public async Task MessageEnvelope_GetAllPolicyDecisions_SkipsHopsWithoutTrailsAsync() {
    // Arrange
    var trail1 = new PolicyDecisionTrail();
    trail1.RecordDecision("Policy1", "Rule1", true, null, "First decision");

    var trail3 = new PolicyDecisionTrail();
    trail3.RecordDecision("Policy3", "Rule3", true, null, "Third decision");

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [
        new MessageHop {

          ServiceInstance = new ServiceInstanceInfo {

            ServiceName = "Hop1",

            InstanceId = Guid.NewGuid(),

            HostName = "test-host",

            ProcessId = 12345

          }, Trail = trail1 }
      ]
    };

    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Hop2",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Trail = null
    }); // No trail
    envelope.AddHop(new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "Hop3",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Trail = trail3
    });

    // Act
    var decisions = envelope.GetAllPolicyDecisions();

    // Assert
    await Assert.That(decisions).Count().IsEqualTo(2);
    await Assert.That(decisions[0].Reason).IsEqualTo("First decision");
    await Assert.That(decisions[1].Reason).IsEqualTo("Third decision");
  }

  #endregion

  #region MessageHop Tests

  [Test]
  public async Task MessageHop_Constructor_SetsAllPropertiesAsync() {
    // Arrange
    var timestamp = DateTimeOffset.UtcNow;
    var securityContext = new SecurityContext {
      UserId = "user-123",
      TenantId = "tenant-abc"
    };

    // Act
    var hop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "test-machine",
        ProcessId = 12345
      },
      Timestamp = timestamp,
      Topic = "test-topic",
      StreamKey = "test-stream",
      PartitionIndex = 5,
      SequenceNumber = 100,
      ExecutionStrategy = "ParallelExecutor",
      SecurityContext = securityContext,
      CallerMemberName = "ExecuteAsync",
      CallerFilePath = "/src/SerialExecutor.cs",
      CallerLineNumber = 42,
      Duration = TimeSpan.FromMilliseconds(150)
    };

    // Assert
    await Assert.That(hop.ServiceInstance.ServiceName).IsEqualTo("TestService");
    await Assert.That(hop.ServiceInstance.HostName).IsEqualTo("test-machine");
    await Assert.That(hop.Timestamp).IsEqualTo(timestamp);
    await Assert.That(hop.Topic).IsEqualTo("test-topic");
    await Assert.That(hop.StreamKey).IsEqualTo("test-stream");
    await Assert.That(hop.PartitionIndex).IsEqualTo(5);
    await Assert.That(hop.SequenceNumber).IsEqualTo(100);
    await Assert.That(hop.ExecutionStrategy).IsEqualTo("ParallelExecutor");
    await Assert.That(hop.SecurityContext).IsEqualTo(securityContext);
    await Assert.That(hop.CallerMemberName).IsEqualTo("ExecuteAsync");
    await Assert.That(hop.CallerFilePath).IsEqualTo("/src/SerialExecutor.cs");
    await Assert.That(hop.CallerLineNumber).IsEqualTo(42);
    await Assert.That(hop.Duration).IsEqualTo(TimeSpan.FromMilliseconds(150));
  }

  [Test]
  public async Task MessageHop_CallerInfo_CanBeNullAsync() {
    // Arrange & Act
    var hop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      CallerMemberName = null,
      CallerFilePath = null,
      CallerLineNumber = null
    };

    // Assert
    await Assert.That(hop.CallerMemberName).IsNull();
    await Assert.That(hop.CallerFilePath).IsNull();
    await Assert.That(hop.CallerLineNumber).IsNull();
  }

  [Test]
  public async Task MessageHop_SecurityContext_CanBeNullAsync() {
    // Arrange & Act
    var hop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      SecurityContext = null
    };

    // Assert
    await Assert.That(hop.SecurityContext).IsNull();
  }

  [Test]
  public async Task MessageHop_SecurityContext_CanBeSetAsync() {
    // Arrange
    var securityContext = new SecurityContext {
      UserId = "user-123",
      TenantId = "tenant-abc"
    };

    // Act
    var hop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      SecurityContext = securityContext
    };

    // Assert
    await Assert.That(hop.SecurityContext).IsEqualTo(securityContext);
    await Assert.That(hop.SecurityContext!.UserId).IsEqualTo("user-123");
    await Assert.That(hop.SecurityContext!.TenantId).IsEqualTo("tenant-abc");
  }

  [Test]
  public async Task MessageHop_Trail_CanBeNullAsync() {
    // Arrange & Act
    var hop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Trail = null
    };

    // Assert
    await Assert.That(hop.Trail).IsNull();
  }

  [Test]
  public async Task MessageHop_Trail_CanBeSetAsync() {
    // Arrange
    var trail = new PolicyDecisionTrail();
    trail.RecordDecision("TestPolicy", "TestRule", true, "TestConfig", "Test reason");

    // Act
    var hop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Trail = trail
    };

    // Assert
    await Assert.That(hop.Trail).IsEqualTo(trail);
    await Assert.That(hop.Trail!.Decisions).Count().IsEqualTo(1);
    await Assert.That(hop.Trail!.Decisions[0].PolicyName).IsEqualTo("TestPolicy");
  }

  [Test]
  public async Task MessageHop_Type_DefaultsToCurrentAsync() {
    // Arrange & Act
    var hop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      }
    };

    // Assert
    await Assert.That(hop.Type).IsEqualTo(HopType.Current);
  }

  [Test]
  public async Task MessageHop_Type_CanBeSetToCausationAsync() {
    // Arrange & Act
    var hop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Type = HopType.Causation,
      CausationId = MessageId.New(),
      CausationType = "OrderCreated"
    };

    // Assert
    await Assert.That(hop.Type).IsEqualTo(HopType.Causation);
    await Assert.That(hop.CausationId).IsNotNull();
    await Assert.That(hop.CausationType).IsEqualTo("OrderCreated");
  }

  [Test]
  public async Task MessageHop_CausationFields_AreNullForCurrentHopsAsync() {
    // Arrange & Act
    var hop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      },
      Type = HopType.Current
    };

    // Assert
    await Assert.That(hop.CausationId).IsNull();
    await Assert.That(hop.CausationType).IsNull();
  }

  #endregion

  #region Causation Hop Tests

  [Test]
  public async Task MessageEnvelope_GetCausationHops_ReturnsEmpty_WhenNoCausationHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [
        new MessageHop {

          ServiceInstance = new ServiceInstanceInfo {

            ServiceName = "Service1",

            InstanceId = Guid.NewGuid(),

            HostName = "test-host",

            ProcessId = 12345

          }, Type = HopType.Current },
        new MessageHop {

          ServiceInstance = new ServiceInstanceInfo {

            ServiceName = "Service2",

            InstanceId = Guid.NewGuid(),

            HostName = "test-host",

            ProcessId = 12345

          }, Type = HopType.Current }
      ]
    };

    // Act
    var causationHops = envelope.GetCausationHops();

    // Assert
    await Assert.That(causationHops).IsEmpty();
  }

  [Test]
  public async Task MessageEnvelope_GetCausationHops_ReturnsOnlyCausationHopsAsync() {
    // Arrange
    var causationId1 = MessageId.New();
    var causationId2 = MessageId.New();

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [
        new MessageHop {

          ServiceInstance = new ServiceInstanceInfo {

            ServiceName = "CausationHop1",

            InstanceId = Guid.NewGuid(),

            HostName = "test-host",

            ProcessId = 12345

          }, Type = HopType.Causation, CausationId = causationId1, CausationType = "OrderCreated" },
        new MessageHop {

          ServiceInstance = new ServiceInstanceInfo {

            ServiceName = "CurrentHop1",

            InstanceId = Guid.NewGuid(),

            HostName = "test-host",

            ProcessId = 12345

          }, Type = HopType.Current },
        new MessageHop {

          ServiceInstance = new ServiceInstanceInfo {

            ServiceName = "CausationHop2",

            InstanceId = Guid.NewGuid(),

            HostName = "test-host",

            ProcessId = 12345

          }, Type = HopType.Causation, CausationId = causationId2, CausationType = "PaymentProcessed" },
        new MessageHop {

          ServiceInstance = new ServiceInstanceInfo {

            ServiceName = "CurrentHop2",

            InstanceId = Guid.NewGuid(),

            HostName = "test-host",

            ProcessId = 12345

          }, Type = HopType.Current }
      ]
    };

    // Act
    var causationHops = envelope.GetCausationHops();

    // Assert
    await Assert.That(causationHops).Count().IsEqualTo(2);
    await Assert.That(causationHops[0].ServiceInstance.ServiceName).IsEqualTo("CausationHop1");
    await Assert.That(causationHops[0].CausationId).IsEqualTo(causationId1);
    await Assert.That(causationHops[0].CausationType).IsEqualTo("OrderCreated");
    await Assert.That(causationHops[1].ServiceInstance.ServiceName).IsEqualTo("CausationHop2");
    await Assert.That(causationHops[1].CausationId).IsEqualTo(causationId2);
    await Assert.That(causationHops[1].CausationType).IsEqualTo("PaymentProcessed");
  }

  [Test]
  public async Task MessageEnvelope_GetCurrentHops_ReturnsOnlyCurrentHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [
        new MessageHop {

          ServiceInstance = new ServiceInstanceInfo {

            ServiceName = "CausationHop1",

            InstanceId = Guid.NewGuid(),

            HostName = "test-host",

            ProcessId = 12345

          }, Type = HopType.Causation },
        new MessageHop {

          ServiceInstance = new ServiceInstanceInfo {

            ServiceName = "CurrentHop1",

            InstanceId = Guid.NewGuid(),

            HostName = "test-host",

            ProcessId = 12345

          }, Type = HopType.Current },
        new MessageHop {

          ServiceInstance = new ServiceInstanceInfo {

            ServiceName = "CausationHop2",

            InstanceId = Guid.NewGuid(),

            HostName = "test-host",

            ProcessId = 12345

          }, Type = HopType.Causation },
        new MessageHop {

          ServiceInstance = new ServiceInstanceInfo {

            ServiceName = "CurrentHop2",

            InstanceId = Guid.NewGuid(),

            HostName = "test-host",

            ProcessId = 12345

          }, Type = HopType.Current }
      ]
    };

    // Act
    var currentHops = envelope.GetCurrentHops();

    // Assert
    await Assert.That(currentHops).Count().IsEqualTo(2);
    await Assert.That(currentHops[0].ServiceInstance.ServiceName).IsEqualTo("CurrentHop1");
    await Assert.That(currentHops[1].ServiceInstance.ServiceName).IsEqualTo("CurrentHop2");
  }

  [Test]
  public async Task MessageEnvelope_GetCurrentTopic_IgnoresCausationHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [
        new MessageHop {

          ServiceInstance = new ServiceInstanceInfo {

            ServiceName = "CausationHop",

            InstanceId = Guid.NewGuid(),

            HostName = "test-host",

            ProcessId = 12345

          }, Type = HopType.Causation, Topic = "old-topic" },
        new MessageHop {

          ServiceInstance = new ServiceInstanceInfo {

            ServiceName = "CurrentHop",

            InstanceId = Guid.NewGuid(),

            HostName = "test-host",

            ProcessId = 12345

          }, Type = HopType.Current, Topic = "current-topic" }
      ]
    };

    // Act
    var topic = envelope.GetCurrentTopic();

    // Assert
    await Assert.That(topic).IsEqualTo("current-topic");
  }

  [Test]
  public async Task MessageEnvelope_GetCurrentStreamKey_IgnoresCausationHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [
        new MessageHop {

          ServiceInstance = new ServiceInstanceInfo {

            ServiceName = "CausationHop",

            InstanceId = Guid.NewGuid(),

            HostName = "test-host",

            ProcessId = 12345

          }, Type = HopType.Causation, StreamKey = "old-stream" },
        new MessageHop {

          ServiceInstance = new ServiceInstanceInfo {

            ServiceName = "CurrentHop",

            InstanceId = Guid.NewGuid(),

            HostName = "test-host",

            ProcessId = 12345

          }, Type = HopType.Current, StreamKey = "current-stream" }
      ]
    };

    // Act
    var streamKey = envelope.GetCurrentStreamKey();

    // Assert
    await Assert.That(streamKey).IsEqualTo("current-stream");
  }

  [Test]
  public async Task MessageEnvelope_GetCurrentPartitionIndex_IgnoresCausationHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [
        new MessageHop {

          ServiceInstance = new ServiceInstanceInfo {

            ServiceName = "CausationHop",

            InstanceId = Guid.NewGuid(),

            HostName = "test-host",

            ProcessId = 12345

          }, Type = HopType.Causation, PartitionIndex = 5 },
        new MessageHop {

          ServiceInstance = new ServiceInstanceInfo {

            ServiceName = "CurrentHop",

            InstanceId = Guid.NewGuid(),

            HostName = "test-host",

            ProcessId = 12345

          }, Type = HopType.Current, PartitionIndex = 3 }
      ]
    };

    // Act
    var partitionIndex = envelope.GetCurrentPartitionIndex();

    // Assert
    await Assert.That(partitionIndex).IsEqualTo(3);
  }

  [Test]
  public async Task MessageEnvelope_GetCurrentSequenceNumber_IgnoresCausationHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [
        new MessageHop {

          ServiceInstance = new ServiceInstanceInfo {

            ServiceName = "CausationHop",

            InstanceId = Guid.NewGuid(),

            HostName = "test-host",

            ProcessId = 12345

          }, Type = HopType.Causation, SequenceNumber = 100 },
        new MessageHop {

          ServiceInstance = new ServiceInstanceInfo {

            ServiceName = "CurrentHop",

            InstanceId = Guid.NewGuid(),

            HostName = "test-host",

            ProcessId = 12345

          }, Type = HopType.Current, SequenceNumber = 200 }
      ]
    };

    // Act
    var sequenceNumber = envelope.GetCurrentSequenceNumber();

    // Assert
    await Assert.That(sequenceNumber).IsEqualTo(200);
  }

  [Test]
  public async Task MessageEnvelope_GetCurrentSecurityContext_IgnoresCausationHopsAsync() {
    // Arrange
    var causationContext = new SecurityContext { UserId = "old-user", TenantId = "old-tenant" };
    var currentContext = new SecurityContext { UserId = "current-user", TenantId = "current-tenant" };

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [
        new MessageHop {

          ServiceInstance = new ServiceInstanceInfo {

            ServiceName = "CausationHop",

            InstanceId = Guid.NewGuid(),

            HostName = "test-host",

            ProcessId = 12345

          }, Type = HopType.Causation, SecurityContext = causationContext },
        new MessageHop {

          ServiceInstance = new ServiceInstanceInfo {

            ServiceName = "CurrentHop",

            InstanceId = Guid.NewGuid(),

            HostName = "test-host",

            ProcessId = 12345

          }, Type = HopType.Current, SecurityContext = currentContext }
      ]
    };

    // Act
    var securityContext = envelope.GetCurrentSecurityContext();

    // Assert
    await Assert.That(securityContext).IsEqualTo(currentContext);
    await Assert.That(securityContext!.UserId).IsEqualTo("current-user");
  }

  [Test]
  [RequiresUnreferencedCode("")]
  [RequiresDynamicCode("")]
  public async Task MessageEnvelope_GetMetadata_IgnoresCausationHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "CausationHop",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Type = HopType.Causation,
          Metadata = new Dictionary<string, JsonElement> { ["key"] = JsonSerializer.SerializeToElement("old-value") }
        },
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "CurrentHop",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Type = HopType.Current,
          Metadata = new Dictionary<string, JsonElement> { ["key"] = JsonSerializer.SerializeToElement("current-value") }
        }
      ]
    };

    // Act
    var value = envelope.GetMetadata("key");

    // Assert
    await Assert.That(value!.Value.GetString()).IsEqualTo("current-value");
  }

  [Test]
  [RequiresUnreferencedCode("")]
  [RequiresDynamicCode("")]
  public async Task MessageEnvelope_GetAllMetadata_IgnoresCausationHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "CausationHop",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Type = HopType.Causation,
          Metadata = new Dictionary<string, JsonElement> { ["old-key"] = JsonSerializer.SerializeToElement("old-value") }
        },
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "CurrentHop",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Type = HopType.Current,
          Metadata = new Dictionary<string, JsonElement> { ["current-key"] = JsonSerializer.SerializeToElement("current-value") }
        }
      ]
    };

    // Act
    var metadata = envelope.GetAllMetadata();

    // Assert
    await Assert.That(metadata).Count().IsEqualTo(1);
    await Assert.That(metadata.ContainsKey("current-key")).IsTrue();
    await Assert.That(metadata.ContainsKey("old-key")).IsFalse();
  }

  [Test]
  public async Task MessageEnvelope_GetAllPolicyDecisions_IgnoresCausationHopsAsync() {
    // Arrange
    var causationTrail = new PolicyDecisionTrail();
    causationTrail.RecordDecision("OldPolicy", "OldRule", true, null, "Old decision");

    var currentTrail = new PolicyDecisionTrail();
    currentTrail.RecordDecision("CurrentPolicy", "CurrentRule", true, null, "Current decision");

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [
        new MessageHop {

          ServiceInstance = new ServiceInstanceInfo {

            ServiceName = "CausationHop",

            InstanceId = Guid.NewGuid(),

            HostName = "test-host",

            ProcessId = 12345

          }, Type = HopType.Causation, Trail = causationTrail },
        new MessageHop {

          ServiceInstance = new ServiceInstanceInfo {

            ServiceName = "CurrentHop",

            InstanceId = Guid.NewGuid(),

            HostName = "test-host",

            ProcessId = 12345

          }, Type = HopType.Current, Trail = currentTrail }
      ]
    };

    // Act
    var decisions = envelope.GetAllPolicyDecisions();

    // Assert
    await Assert.That(decisions).Count().IsEqualTo(1);
    await Assert.That(decisions[0].PolicyName).IsEqualTo("CurrentPolicy");
  }

  #endregion

  #region MessageTracing Tests

  [Test]
  public async Task RecordHop_CapturesCallerMemberName_AutomaticallyAsync() {
    // Arrange & Act
    var hop = _testMethod_ThatRecordsHop();

    // Assert
    await Assert.That(hop.CallerMemberName).IsEqualTo(nameof(_testMethod_ThatRecordsHop));
  }

  [Test]
  public async Task RecordHop_CapturesCallerFilePath_AutomaticallyAsync() {
    // Arrange & Act
    var hop = _testMethod_ThatRecordsHop();

    // Assert
    await Assert.That(hop.CallerFilePath).IsNotNull();
    await Assert.That(hop.CallerFilePath).EndsWith("MessageTracingTests.cs");
  }

  [Test]
  public async Task RecordHop_CapturesCallerLineNumber_AutomaticallyAsync() {
    // Arrange & Act
    var hop = _testMethod_ThatRecordsHop();

    // Assert
    await Assert.That(hop.CallerLineNumber).IsNotNull();
    await Assert.That(hop.CallerLineNumber!.Value).IsGreaterThan(0);
  }

  [Test]
  public async Task RecordHop_SetsServiceName_ToEntryAssemblyAsync() {
    // Arrange
    var serviceInstance = new ServiceInstanceInfo {
      ServiceName = "TestService",
      InstanceId = Guid.NewGuid(),
      HostName = "test-host",
      ProcessId = 12345
    };

    // Act
    var hop = MessageTracing.RecordHop(serviceInstance, "test-topic", "test-stream", "TestExecutor");

    // Assert
    await Assert.That(hop.ServiceInstance.ServiceName).IsNotNull();
    await Assert.That(hop.ServiceInstance.ServiceName).IsNotEqualTo("Unknown");
  }

  [Test]
  public async Task RecordHop_SetsMachineName_ToEnvironmentMachineNameAsync() {
    // Arrange
    var serviceInstance = new ServiceInstanceInfo {
      ServiceName = "TestService",
      InstanceId = Guid.NewGuid(),
      HostName = Environment.MachineName,
      ProcessId = 12345
    };

    // Act
    var hop = MessageTracing.RecordHop(serviceInstance, "test-topic", "test-stream", "TestExecutor");

    // Assert
    await Assert.That(hop.ServiceInstance.HostName).IsEqualTo(Environment.MachineName);
  }

  [Test]
  public async Task RecordHop_SetsTimestamp_ToApproximatelyNowAsync() {
    // Arrange
    var serviceInstance = new ServiceInstanceInfo {
      ServiceName = "TestService",
      InstanceId = Guid.NewGuid(),
      HostName = "test-host",
      ProcessId = 12345
    };
    var before = DateTimeOffset.UtcNow;

    // Act
    var hop = MessageTracing.RecordHop(serviceInstance, "test-topic", "test-stream", "TestExecutor");

    // Assert
    var after = DateTimeOffset.UtcNow;
    await Assert.That(hop.Timestamp).IsGreaterThanOrEqualTo(before);
    await Assert.That(hop.Timestamp).IsLessThanOrEqualTo(after);
  }

  [Test]
  public async Task RecordHop_SetsTopicStreamAndStrategyAsync() {
    // Arrange
    var serviceInstance = new ServiceInstanceInfo {
      ServiceName = "TestService",
      InstanceId = Guid.NewGuid(),
      HostName = "test-host",
      ProcessId = 12345
    };

    // Act
    var hop = MessageTracing.RecordHop(serviceInstance, "orders", "order-123", "SerialExecutor");

    // Assert
    await Assert.That(hop.Topic).IsEqualTo("orders");
    await Assert.That(hop.StreamKey).IsEqualTo("order-123");
    await Assert.That(hop.ExecutionStrategy).IsEqualTo("SerialExecutor");
  }

  [Test]
  public async Task RecordHop_FromDifferentMethods_CapturesDifferentCallerInfoAsync() {
    // Arrange & Act
    var hop1 = _testMethod_ThatRecordsHop();
    var hop2 = _anotherTestMethod_ThatRecordsHop();

    // Assert
    await Assert.That(hop1.CallerMemberName).IsEqualTo(nameof(_testMethod_ThatRecordsHop));
    await Assert.That(hop2.CallerMemberName).IsEqualTo(nameof(_anotherTestMethod_ThatRecordsHop));
    await Assert.That(hop1.CallerLineNumber).IsNotEqualTo(hop2.CallerLineNumber);
  }

  [Test]
  public async Task RecordHop_WithPartitionAndSequence_SetsOptionalFieldsAsync() {
    // Arrange
    var serviceInstance = new ServiceInstanceInfo {
      ServiceName = "TestService",
      InstanceId = Guid.NewGuid(),
      HostName = "test-host",
      ProcessId = 12345
    };

    // Act
    var hop = MessageTracing.RecordHop(
      serviceInstance: serviceInstance,
      topic: "test-topic",
      streamKey: "test-stream",
      executionStrategy: "TestExecutor",
      partitionIndex: 7,
      sequenceNumber: 999
    );

    // Assert
    await Assert.That(hop.PartitionIndex).IsEqualTo(7);
    await Assert.That(hop.SequenceNumber).IsEqualTo(999);
  }

  [Test]
  public async Task RecordHop_WithDuration_SetsDurationFieldAsync() {
    // Arrange
    var serviceInstance = new ServiceInstanceInfo {
      ServiceName = "TestService",
      InstanceId = Guid.NewGuid(),
      HostName = "test-host",
      ProcessId = 12345
    };

    // Act
    var hop = MessageTracing.RecordHop(
      serviceInstance: serviceInstance,
      topic: "test-topic",
      streamKey: "test-stream",
      executionStrategy: "TestExecutor",
      duration: TimeSpan.FromMilliseconds(250)
    );

    // Assert
    await Assert.That(hop.Duration).IsEqualTo(TimeSpan.FromMilliseconds(250));
  }

  #endregion

  #region MessageTrace Tests

  [Test]
  public async Task MessageTrace_Constructor_InitializesWithMessageIdAsync() {
    // Arrange
    var messageId = MessageId.New();

    // Act
    var trace = new MessageTrace(messageId);

    // Assert
    await Assert.That(trace.MessageId).IsEqualTo(messageId);
  }

  [Test]
  public async Task MessageTrace_Hops_IsInitializedEmptyAsync() {
    // Arrange & Act
    var trace = new MessageTrace(MessageId.New());

    // Assert
    await Assert.That(trace.Hops).IsNotNull();
    await Assert.That(trace.Hops).IsEmpty();
  }

  [Test]
  public async Task MessageTrace_PolicyTrails_IsInitializedEmptyAsync() {
    // Arrange & Act
    var trace = new MessageTrace(MessageId.New());

    // Assert
    await Assert.That(trace.PolicyTrails).IsNotNull();
    await Assert.That(trace.PolicyTrails).IsEmpty();
  }

  [Test]
  public async Task MessageTrace_AddHop_AddsToHopsListAsync() {
    // Arrange
    var trace = new MessageTrace(MessageId.New());
    var hop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "test-host",
        ProcessId = 12345
      }
    };

    // Act
    trace.AddHop(hop);

    // Assert
    await Assert.That(trace.Hops).Count().IsEqualTo(1);
    await Assert.That(trace.Hops[0]).IsEqualTo(hop);
  }

  [Test]
  public async Task MessageTrace_AddPolicyTrail_AddsToTrailsListAsync() {
    // Arrange
    var trace = new MessageTrace(MessageId.New());
    var trail = new PolicyDecisionTrail();
    trail.RecordDecision("TestPolicy", "TestRule", true, null, "Test reason");

    // Act
    trace.AddPolicyTrail(trail);

    // Assert
    await Assert.That(trace.PolicyTrails).Count().IsEqualTo(1);
    await Assert.That(trace.PolicyTrails[0]).IsEqualTo(trail);
  }

  [Test]
  public async Task MessageTrace_SetOutcome_SetsSuccessAndErrorAsync() {
    // Arrange
    var trace = new MessageTrace(MessageId.New());
    var error = new InvalidOperationException("Test error");

    // Act
    trace.SetOutcome(success: false, error: error);

    // Assert
    await Assert.That(trace.Success).IsFalse();
    await Assert.That(trace.Error).IsEqualTo(error);
  }

  [Test]
  public async Task MessageTrace_RecordTiming_AddsToDictionaryAsync() {
    // Arrange
    var trace = new MessageTrace(MessageId.New());

    // Act
    trace.RecordTiming("policy-evaluation", TimeSpan.FromMilliseconds(10));
    trace.RecordTiming("handler-execution", TimeSpan.FromMilliseconds(100));

    // Assert
    await Assert.That(trace.Timings).Count().IsEqualTo(2);
    await Assert.That(trace.Timings["policy-evaluation"]).IsEqualTo(TimeSpan.FromMilliseconds(10));
    await Assert.That(trace.Timings["handler-execution"]).IsEqualTo(TimeSpan.FromMilliseconds(100));
  }

  [Test]
  public async Task MessageTrace_TotalDuration_CanBeSetAsync() {
    // Arrange
    var trace = new MessageTrace(MessageId.New()) {
      // Act
      TotalDuration = TimeSpan.FromMilliseconds(500)
    };

    // Assert
    await Assert.That(trace.TotalDuration).IsEqualTo(TimeSpan.FromMilliseconds(500));
  }

  [Test]
  public async Task MessageTrace_WithCorrelationAndCausation_SetsPropertiesAsync() {
    // Arrange
    var messageId = MessageId.New();
    var correlationId = CorrelationId.New();
    var causationId = MessageId.New();

    // Act
    var trace = new MessageTrace(messageId) {
      CorrelationId = correlationId,
      CausationId = causationId
    };

    // Assert
    await Assert.That(trace.MessageId).IsEqualTo(messageId);
    await Assert.That(trace.CorrelationId).IsEqualTo(correlationId);
    await Assert.That(trace.CausationId).IsEqualTo(causationId);
  }

  #endregion

  // Helper methods for testing caller info capture
  private static MessageHop _testMethod_ThatRecordsHop() {
    var serviceInstance = new ServiceInstanceInfo {
      ServiceName = "TestService",
      InstanceId = Guid.NewGuid(),
      HostName = "test-host",
      ProcessId = 12345
    };
    return MessageTracing.RecordHop(serviceInstance, "test-topic", "test-stream", "TestExecutor");
  }

  private static MessageHop _anotherTestMethod_ThatRecordsHop() {
    var serviceInstance = new ServiceInstanceInfo {
      ServiceName = "TestService",
      InstanceId = Guid.NewGuid(),
      HostName = "test-host",
      ProcessId = 12345
    };
    return MessageTracing.RecordHop(serviceInstance, "test-topic", "test-stream", "TestExecutor");
  }
}
