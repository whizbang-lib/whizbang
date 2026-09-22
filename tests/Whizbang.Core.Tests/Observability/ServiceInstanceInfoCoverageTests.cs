using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Coverage-round tests for the four required-property guards inside
/// <see cref="ServiceInstanceInfoConverter.Read"/>, plus the malformed-token guard ahead of them.
/// Every existing round-trip test always serializes a complete envelope, so none of these
/// "missing required property" branches is ever taken.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/ServiceInstanceInfo.cs</code-under-test>
public class ServiceInstanceInfoCoverageTests {

  // ServiceInstanceInfo identifies which service instance handled a hop; each of these guards turns
  // a truncated or hand-edited wire payload into a clear, boundary-level JsonException instead of a
  // null or wrong ServiceInstanceInfo silently propagating into trace and DLQ output, where "which
  // instance did this" can no longer be trusted.

  [Test]
  public async Task Read_NonObjectToken_ThrowsJsonExceptionAsync() {
    const string json = "[]";

    var exception = await Assert.That(() =>
      JsonSerializer.Deserialize(json, InfrastructureJsonContext.Default.ServiceInstanceInfo))
      .Throws<JsonException>();

    await Assert.That(exception!.Message).Contains("Expected start of object for ServiceInstanceInfo")
      .Because("a non-object payload must fail at the boundary with a clear reason, not deep inside the reader");
  }

  [Test]
  public async Task Read_MissingServiceName_ThrowsJsonExceptionAsync() {
    const string json = """{"ii":"00000000-0000-0000-0000-000000000001","hn":"host-1","pi":100}""";

    var exception = await Assert.That(() =>
      JsonSerializer.Deserialize(json, InfrastructureJsonContext.Default.ServiceInstanceInfo))
      .Throws<JsonException>();

    await Assert.That(exception!.Message).Contains("Missing required property: ServiceName (or sn)")
      .Because("a hop with no identifiable service name must never silently become an empty or null name in trace output");
  }

  [Test]
  public async Task Read_MissingInstanceId_ThrowsJsonExceptionAsync() {
    const string json = """{"sn":"CoverageService","hn":"host-1","pi":100}""";

    var exception = await Assert.That(() =>
      JsonSerializer.Deserialize(json, InfrastructureJsonContext.Default.ServiceInstanceInfo))
      .Throws<JsonException>();

    await Assert.That(exception!.Message).Contains("Missing required property: InstanceId (or ii)")
      .Because("InstanceId disambiguates replica instances of the same service; without the guard two instances could collapse into one untraceable Guid.Empty");
  }

  [Test]
  public async Task Read_MissingHostName_ThrowsJsonExceptionAsync() {
    const string json = """{"sn":"CoverageService","ii":"00000000-0000-0000-0000-000000000001","pi":100}""";

    var exception = await Assert.That(() =>
      JsonSerializer.Deserialize(json, InfrastructureJsonContext.Default.ServiceInstanceInfo))
      .Throws<JsonException>();

    await Assert.That(exception!.Message).Contains("Missing required property: HostName (or hn)")
      .Because("HostName is what an operator greps for when isolating which machine produced a hop; a silently missing value defeats that");
  }

  [Test]
  public async Task Read_MissingProcessId_ThrowsJsonExceptionAsync() {
    const string json = """{"sn":"CoverageService","ii":"00000000-0000-0000-0000-000000000001","hn":"host-1"}""";

    var exception = await Assert.That(() =>
      JsonSerializer.Deserialize(json, InfrastructureJsonContext.Default.ServiceInstanceInfo))
      .Throws<JsonException>();

    await Assert.That(exception!.Message).Contains("Missing required property: ProcessId (or pi)")
      .Because("ProcessId distinguishes instances sharing a host across a restart; a silently missing value hides which process actually handled the hop");
  }
}
