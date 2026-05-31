using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Surface tests for <see cref="ReceptorProcessingCompletion"/> and
/// <see cref="ReceptorProcessingFailure"/> — the two records that carry handler
/// success/failure rows from the in-memory tracking strategy into the SQL
/// complete_receptor_processing function. The coverage report shows 0/13 and
/// 0/17 lines (records generate value-equality machinery — coverage counts each
/// compiler-generated member as a "line").
///
/// These records have `required` properties — the test surface is small but
/// load-bearing: any rename, reorder, or removal here breaks the SQL marshaller
/// silently. Pin the property names + value equality + with-expression copying.
/// </summary>
/// <docs>fundamentals/work-coordinator/receptor-completion</docs>
public class ReceptorProcessingRecordsTests {

  [Test]
  public async Task ReceptorProcessingCompletion_AllPropertiesRoundTripAsync() {
    var eventId = Guid.NewGuid();
    var c = new ReceptorProcessingCompletion {
      EventId = eventId,
      ReceptorName = "MyReceptor",
      Status = ReceptorProcessingStatus.Completed,
    };

    await Assert.That(c.EventId).IsEqualTo(eventId);
    await Assert.That(c.ReceptorName).IsEqualTo("MyReceptor");
    await Assert.That(c.Status).IsEqualTo(ReceptorProcessingStatus.Completed);
  }

  [Test]
  public async Task ReceptorProcessingCompletion_RecordValueEqualityAsync() {
    var eventId = Guid.NewGuid();
    var a = new ReceptorProcessingCompletion {
      EventId = eventId,
      ReceptorName = "R",
      Status = ReceptorProcessingStatus.Completed,
    };
    var b = new ReceptorProcessingCompletion {
      EventId = eventId,
      ReceptorName = "R",
      Status = ReceptorProcessingStatus.Completed,
    };

    await Assert.That(a).IsEqualTo(b);
    await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
  }

  [Test]
  public async Task ReceptorProcessingCompletion_DifferentReceptorName_NotEqualAsync() {
    var eventId = Guid.NewGuid();
    var a = new ReceptorProcessingCompletion {
      EventId = eventId,
      ReceptorName = "A",
      Status = ReceptorProcessingStatus.Completed,
    };
    var b = a with { ReceptorName = "B" };

    await Assert.That(a).IsNotEqualTo(b);
    await Assert.That(b.ReceptorName).IsEqualTo("B");
    await Assert.That(b.EventId).IsEqualTo(a.EventId);
  }

  [Test]
  public async Task ReceptorProcessingFailure_AllPropertiesRoundTripAsync() {
    var eventId = Guid.NewGuid();
    var f = new ReceptorProcessingFailure {
      EventId = eventId,
      ReceptorName = "MyReceptor",
      Status = ReceptorProcessingStatus.Failed,
      Error = "downstream connection refused",
    };

    await Assert.That(f.EventId).IsEqualTo(eventId);
    await Assert.That(f.ReceptorName).IsEqualTo("MyReceptor");
    await Assert.That(f.Status).IsEqualTo(ReceptorProcessingStatus.Failed);
    await Assert.That(f.Error).IsEqualTo("downstream connection refused");
  }

  [Test]
  public async Task ReceptorProcessingFailure_RecordValueEqualityAsync() {
    var eventId = Guid.NewGuid();
    var a = new ReceptorProcessingFailure {
      EventId = eventId,
      ReceptorName = "R",
      Status = ReceptorProcessingStatus.Failed,
      Error = "boom",
    };
    var b = new ReceptorProcessingFailure {
      EventId = eventId,
      ReceptorName = "R",
      Status = ReceptorProcessingStatus.Failed,
      Error = "boom",
    };

    await Assert.That(a).IsEqualTo(b);
    await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
  }

  [Test]
  public async Task ReceptorProcessingFailure_DifferentError_NotEqualAsync() {
    var eventId = Guid.NewGuid();
    var a = new ReceptorProcessingFailure {
      EventId = eventId,
      ReceptorName = "R",
      Status = ReceptorProcessingStatus.Failed,
      Error = "first error",
    };
    var b = a with { Error = "second error" };

    await Assert.That(a).IsNotEqualTo(b);
    await Assert.That(b.Error).IsEqualTo("second error");
  }
}
