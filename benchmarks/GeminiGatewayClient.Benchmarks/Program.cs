using BenchmarkDotNet.Running;

var benchmarkArgs = args.Select(a => string.Equals(a, "ShortRun", StringComparison.OrdinalIgnoreCase) ? "short" : a).ToArray();

if (benchmarkArgs.Length == 0 || !benchmarkArgs.Any(a => a.StartsWith("--filter", StringComparison.OrdinalIgnoreCase)))
{
    benchmarkArgs = [.. benchmarkArgs, "--filter", "*"];
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(benchmarkArgs);
