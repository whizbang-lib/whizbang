using Microsoft.Extensions.Logging;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Focused-port test of the ASB AckAndDrop log-level routing — NoLocalConsumer
/// logs at Debug (routine cross-domain drop), every other AckAndDrop reason
/// logs at Warning (genuine envelope/type/deserialise surprise).
/// The full reusable <c>IMessageDiscardPolicy</c> architecture lives on
/// <c>feat/work-pump-decomposition</c>; this branch carries only the level fix.
/// </summary>
public class AsbAckDropLogLevelTests {

  [Test]
  public async Task LevelFor_NoLocalConsumer_IsDebugAsync() {
    var level = AzureServiceBusTransport.AckDropLogLevelFor(AsbReceiveReason.NO_LOCAL_CONSUMER);
    await Assert.That(level).IsEqualTo(LogLevel.Debug);
  }

  [Test]
  public async Task LevelFor_MissingEnvelopeType_IsWarningAsync() {
    var level = AzureServiceBusTransport.AckDropLogLevelFor(AsbReceiveReason.MISSING_ENVELOPE_TYPE);
    await Assert.That(level).IsEqualTo(LogLevel.Warning);
  }

  [Test]
  public async Task LevelFor_MissingJsonTypeInfo_IsWarningAsync() {
    var level = AzureServiceBusTransport.AckDropLogLevelFor(AsbReceiveReason.MISSING_JSON_TYPE_INFO);
    await Assert.That(level).IsEqualTo(LogLevel.Warning);
  }

  [Test]
  public async Task LevelFor_DeserializationFailed_IsWarningAsync() {
    var level = AzureServiceBusTransport.AckDropLogLevelFor(AsbReceiveReason.DESERIALIZATION_FAILED);
    await Assert.That(level).IsEqualTo(LogLevel.Warning);
  }
}
