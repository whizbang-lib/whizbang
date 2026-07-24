using System;
using System.Text.Json;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Serialization;
using Whizbang.Core.Temporal;

namespace Whizbang.Core.Tests.Archival;

/// <summary>
/// Locks the A1 increment-5 schedule builder: <see cref="StreamCloseSchedule"/> builds a
/// <see cref="ScheduleDefinition"/> whose occurrence type + payload round-trip back to
/// <see cref="ScheduledStreamClose"/> when the F2 engine fires it. (The receptor + registrar wiring is
/// covered in the EFCore.Postgres test project, where the receptor lives.)
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public class StreamCloseScheduleTests {
  [Test]
  public async Task Recurring_BuildsIntervalDefinition_PayloadRoundTripsAsync() {
    var target = Guid.NewGuid();
    var scheduler = Guid.NewGuid();
    var authority = Guid.NewGuid();

    var def = StreamCloseSchedule.Recurring(
      "close-account-123", scheduler, new ScheduledStreamClose(target, 900, true),
      TimeSpan.FromDays(30), authority);

    await Assert.That(def.Key).IsEqualTo("close-account-123");
    await Assert.That(def.StreamId).IsEqualTo(scheduler)
      .Because("The schedule lives in the dedicated control stream, not the target being closed.");
    await Assert.That(def.Kind).IsEqualTo(RecurrenceKind.Interval);
    await Assert.That(def.Interval).IsEqualTo(TimeSpan.FromDays(30));
    await Assert.That(def.AuthorityPrincipalId).IsEqualTo(authority);
    await Assert.That(def.EventType).IsEqualTo(TypeNameFormatter.Format(typeof(ScheduledStreamClose)))
      .Because("The occurrence's routing type must be the assembly-qualified form GetTypeInfoByName resolves.");

    var options = JsonContextRegistry.CreateCombinedOptions();
    var back = (ScheduledStreamClose?)JsonSerializer.Deserialize(
      def.EventDataJson!, options.GetTypeInfo(typeof(ScheduledStreamClose)));
    await Assert.That(back!.StreamId).IsEqualTo(target);
    await Assert.That(back.ThroughVersion).IsEqualTo(900L);
    await Assert.That(back.Archive).IsTrue()
      .Because("The payload round-trips through the framework's JSON options so the fired occurrence deserializes back to the command.");
  }

  [Test]
  public async Task RecurringCron_BuildsCronDefinitionAsync() {
    var def = StreamCloseSchedule.RecurringCron(
      "close-month", Guid.NewGuid(), new ScheduledStreamClose(Guid.NewGuid(), 100, false),
      "0 0 1 * *", Guid.NewGuid(), timeZone: "America/New_York");

    await Assert.That(def.Kind).IsEqualTo(RecurrenceKind.Cron);
    await Assert.That(def.Cron).IsEqualTo("0 0 1 * *");
    await Assert.That(def.TimeZone).IsEqualTo("America/New_York");
    await Assert.That(def.EventType).IsEqualTo(TypeNameFormatter.Format(typeof(ScheduledStreamClose)));
  }
}
