# PerfUnit

PerfUnit is almost a solution looking for a problem. 

It is a C# library that allows you to easily add performance assertions to your existing xUnit tests to ensure a tested function runs within a given performance constraint (either speed, memory usage, or both). 


## Features
- Utilises a `[PerformanceFact]` attribute to replace `[Fact]` unit tests easily.
- Speed and memory assertions are similarly defined using the `[PerfSpeed]` and `[PerfMemory]` attributes. 
- A static **SimpleBenchmarker** class is included and designed for rapid benchmarking. Support for using Benchmark.NET as the backend instead is planned.
- Source Generators are used to inject benchmarking code to achieve testing without resorting to runtime reflection.


## Installation
Add the PerfUnit package to your test project: `dotnet add package PerfUnit`

> [!NOTE]
> Requires xUnit and .Net 6.0 or higher


## Getting Started
PerfUnit is designed to easily extend existing unit tests to add performance constraints.

Given an existing unit test:
```csharp

public class CalculatorTests
{
    [Fact]
    public void Add_ShouldReturnSum()
    {
      Calculator calculator = new();
      var sum = calculator.Add(1,2);
      Assert.Equal(3, sum);
    }
}
```

All we need to do is:
- Make the test class `partial`
- Replace `[Fact]` with `[PerformanceFact]`
- Add a constraint using `[PerfSpeed]` or `[PerfMemory]` or both
- Add the `.Perf()` extension method anywhere in the part of the code you wish to measure.

```csharp
public partial class CalculatorTests
{
    [PerformanceFact]
    [PerfSpeed(MustTake.LessThan, 1, TimeUnit.Milliseconds)]
    public void Add_ShouldReturnSum()
    {
      Calculator calculator = new();
      var sum = calculator.Add(1,2).Perf();
      Assert.Equal(3, sum);
    }
}
```

This will generate the following code behind the scenes:

<details>
  <summary>See generated code</summary>
  
```csharp
public partial class CalculatorTests
{
    [Fact(DisplayName = "Add_ShouldReturnSum")]
    public void Add_ShouldReturnSum_g()
    {
      Calculator calculator = new();
      var sum = calculator.Add(1,2);
      Assert.Equal(3, sum);

      var (benchTime, memory) = SimpleBenchmarker.Run(() =>
        {
          var _dis_ = calculator.Add(1, 2);
        }, 
        new BenchmarkConfig() {ExpectedMaxMemoryBytes = -1, ExpectedMaxTimeMs = 1}
      );

      Assert.True(benchTime < 1000000, $"Expected execution to take < 1.00 ms, but took {Format.FormatTime(benchTime, true)}");

    }
}
```
  
</details>





> [!CAUTION]
> Make sure to add `[assembly: CollectionBehavior(DisableTestParallelization = true)]` somewhere in your test project, or add test classes with performance tests to the same Collection. Running tests in parallel will harm any performance tests. 

---

## Reason for existence
I was playing around with refactoring huge chunks of a project of mine, and realised in several places I'd actually worsened performance in the process. I had been using `Benchmark.NET` to test several of these changes, but realised I could instead roll these into my unit tests; I wasn't trying to eke out every last drop of performance, but needed to ensure my functions ran within reasonable boundaries (e.g. keeping certain methods allocationless, or making sure LINQ operations weren't taking longer than a few milliseconds). 

I decided to use this as an excuse to dabble with source generation and came up with PerfUnit. 
Of course, halfway through the project I stumbled across `NBench` which seems to be exactly what I needed, if a bit verbose. Ah well. 

