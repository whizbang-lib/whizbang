using BenchmarkDotNet.Running;

namespace Whizbang.Benchmarks;

/// <summary>
/// Benchmark runner for Whizbang performance tests.
/// Run with: dotnet run -c Release
/// </summary>
public class Program {
  public static void Main(string[] args) {
    BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
  }
}
