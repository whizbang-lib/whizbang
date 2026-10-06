// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Branch coverage for <see cref="MessageHopConverter.Read"/> on string properties written as an
/// explicit JSON null: topic, stream id and execution strategy read as empty, and a null duration
/// reads as zero, instead of failing to parse a hop some other producer wrote.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/MessageHop.cs</code-under-test>
[Category("Observability")]
public class MessageHopBranchCoverageTests {

  [Test]
  public async Task Read_StringPropertiesWrittenAsJsonNull_ReadAsEmptyAndDurationAsZeroAsync() {
    var options = InfrastructureJsonContext.Default.Options;
    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.Parse("11111111-2222-4333-8444-555555555555"),
        HostName = "localhost",
        ProcessId = 1234
      },
      Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000),
    };
    var json = JsonNode.Parse(JsonSerializer.Serialize(hop, options))!.AsObject();
    json["to"] = null;
    json["st"] = null;
    json["es"] = null;
    json["du"] = null;

    var read = JsonSerializer.Deserialize<MessageHop>(json.ToJsonString(), options)!;

    await Assert.That(read.Topic).IsEqualTo(string.Empty)
      .Because("a null topic is no topic, and the hop's topic is a non-null string");
    await Assert.That(read.StreamId).IsEqualTo(string.Empty);
    await Assert.That(read.ExecutionStrategy).IsEqualTo(string.Empty);
    await Assert.That(read.Duration).IsEqualTo(TimeSpan.Zero)
      .Because("a null duration parses as zero rather than failing the whole hop");
    await Assert.That(read.ServiceInstance.ServiceName).IsEqualTo("TestService")
      .Because("the rest of the hop still reads normally around the null properties");
  }
}
