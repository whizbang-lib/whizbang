using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.DependencyInjection;

namespace Whizbang.Core.Tests.DependencyInjection;

/// <summary>
/// The startup exception that reports what a service needed and did not get.
/// <para>
/// This is thrown while wiring the container, before anything runs, and the message is the only
/// thing an operator sees. Its job is to name both halves of each gap — the type that needed a
/// dependency and the dependency that was missing — because "a service was not registered" without
/// saying which service needed it leaves someone reading a registration graph by hand.
/// </para>
/// <para>
/// The alternate constructors exist for the standard exception shape and were untested, so nothing
/// held them to leaving <c>Missing</c> in a usable state. An empty list rather than null matters:
/// callers enumerate it to report the gaps, and a null there turns a helpful startup failure into a
/// NullReferenceException on the way out.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/DependencyInjection/WhizbangRegistrationException.cs</code-under-test>
public class WhizbangRegistrationExceptionTests {

  [Test]
  public async Task TheDetailForm_NamesBothHalvesOfEachGapAsync() {
    var missing = new[] {
      new MissingRegistration(typeof(WhizbangRegistrationExceptionTests), typeof(IDisposable)),
    };

    var ex = new WhizbangRegistrationException(missing);

    await Assert.That(ex.Missing.Count).IsEqualTo(1);
    await Assert.That(ex.Message).Contains(nameof(WhizbangRegistrationExceptionTests))
      .Because("naming only the missing service leaves the reader to work out who wanted it");
    await Assert.That(ex.Message).Contains(nameof(IDisposable));
  }

  [Test]
  public async Task TheStandardForms_LeaveMissingEnumerableRatherThanNullAsync() {
    var cause = new InvalidOperationException("inner");
    var forms = new WhizbangRegistrationException[] {
      new(),
      new("registration failed"),
      new("registration failed", cause),
    };

    foreach (var ex in forms) {
      await Assert.That(ex.Missing).IsNotNull()
        .Because("callers enumerate Missing to report the gaps; a null turns a helpful startup "
               + "failure into a NullReferenceException on the way out");
      await Assert.That(ex.Missing).IsEmpty();
      await Assert.That(ex.Message).IsNotNull().And.IsNotEmpty();
    }
    await Assert.That(forms[2].InnerException).IsSameReferenceAs(cause);
  }
}
