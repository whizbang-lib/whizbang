using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// The exception raised when the startup pipeline itself cannot be resolved.
/// <para>
/// This fires before any step runs — a dependency between steps that cannot be satisfied, or a step
/// naming one that was never registered. It aborts startup, so its message is the whole of what an
/// operator gets, and the inner exception is what says which resolution failed underneath.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/StartupStepDescriptor.cs</code-under-test>
public class StartupPipelineConfigurationExceptionTests {

  [Test]
  public async Task TheWrappingForm_KeepsTheUnderlyingResolutionFailureAsync() {
    var cause = new InvalidOperationException("step 'Migrate' depends on unregistered 'Seed'");

    var ex = new StartupPipelineConfigurationException("startup pipeline cannot be ordered", cause);

    await Assert.That(ex.Message).IsEqualTo("startup pipeline cannot be ordered");
    await Assert.That(ex.InnerException).IsSameReferenceAs(cause)
      .Because("the outer message says the pipeline is unresolvable and the inner one says which "
             + "dependency did it — dropping the inner leaves an operator guessing");
  }

  [Test]
  public async Task TheParameterlessForm_StillCarriesAUsableMessageAsync() {
    var ex = new StartupPipelineConfigurationException();

    await Assert.That(ex.Message).IsNotNull().And.IsNotEmpty()
      .Because("this aborts startup, so a bare type name in the log is the entire diagnosis");
  }
}
