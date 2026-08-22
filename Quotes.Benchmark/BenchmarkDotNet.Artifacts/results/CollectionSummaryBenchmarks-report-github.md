```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2/2025Update/HudsonValley2)
12th Gen Intel Core i5-1235U 1.30GHz, 1 CPU, 12 logical and 10 physical cores
.NET SDK 10.0.302
  [Host]     : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  Job-AGBPQC : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

IterationCount=15  WarmupCount=3  

```
| Method          | Mean     | Error     | StdDev    | Ratio | RatioSD | Gen0    | Gen1   | Allocated | Alloc Ratio |
|---------------- |---------:|----------:|----------:|------:|--------:|--------:|-------:|----------:|------------:|
| EntityFramework | 507.8 μs | 153.38 μs | 128.08 μs |  1.05 |    0.34 | 17.5781 | 1.9531 | 112.79 KB |        1.00 |
| Dapper          | 223.9 μs |   6.92 μs |   5.78 μs |  0.46 |    0.10 | 11.7188 | 1.9531 |  73.57 KB |        0.65 |
