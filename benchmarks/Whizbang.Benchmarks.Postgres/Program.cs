using BenchmarkDotNet.Running;

namespace Whizbang.Benchmarks.Postgres;

internal static class Program {
  private static int Main(string[] args) {
    var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    return summaries is null ? 1 : 0;
  }
}
