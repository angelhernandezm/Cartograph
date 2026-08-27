using BenchmarkDotNet.Running;

namespace Cartograph.Benchmarks;

/// <summary>Entry point. Run all benchmarks, or filter with <c>--filter</c>.</summary>
public static class Program
{
    public static void Main(string[] args)
        => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
