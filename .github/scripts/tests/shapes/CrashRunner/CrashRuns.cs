namespace CrashRunner;

// The crash: the runtime ends the process on the spot, as the Internal CLR error in #2446 did. Nothing after it
// runs, no test result is reported, and only a crash dump is left to say what the process was doing.
public class Crash {
  [Test]
  public void FailsFast() {
    Environment.FailFast("CrashRunner: a deliberate crash (#2446)");
  }
}
