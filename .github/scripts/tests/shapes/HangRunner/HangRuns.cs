namespace HangRunner;

// The hang: a wait nothing ever completes. Only the hang dump ends this module.
public class Hang {
  [Test]
  public async Task NeverCompletesAsync() {
    await new TaskCompletionSource().Task;
  }
}
