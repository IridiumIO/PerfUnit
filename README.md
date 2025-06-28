# PerfUnit

PerfUnit is a C# library that allows you to easily add performance assertions to your existing xUnit tests to ensure a tested function runs within a given performance constraint (either speed, memory usage, or both). 

It is almost a solution looking for a problem. 

## Features
- Utilises a `[PerformanceFact]` attribute to replace `[Fact]` unit tests easily.
- Speed and memory assertions are similarly defined using the `[PerfSpeed]` and `[PerfMemory]` attributes with semi fluent-style implementations, e.g. `[PerfSpeed(MustTake.LessThan, 10, TimeUnit.Nanoseconds)]`. 
- A static **SimpleBenchmarker** class is included and designed for rapid benchmarking. Support for using Benchmark.NET as the backend instead is planned.
- Source Generators are used to inject benchmarking code to achieve testing without resorting to runtime reflection.


## Installation

`dotnet add package PerfUnit`

### Requirements
- xUnit
- .Net 6.0 or higher


## Getting Started
PerfUnit is designed to easily extend existing unit tests to add performance constraints.

1. **Given an existing unit test**
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

&nbsp;

2. **All we need to do is**
- Make the test class `partial`
- Replace `[Fact]` with `[PerformanceFact]`
- Add a constraint using `[PerfSpeed]` or `[PerfMemory]` or both
- Add the `.Perf()` extension method into the line of code you wish to measure.

```csharp
public partial class CalculatorTests
{
    [PerformanceFact]
    [PerfSpeed(MustTake.LessThan, 1, TimeUnit.Milliseconds)]
    [PerfMemory(MustUse.LessThanOrEqualTo, 8, SizeUnit.Bytes)]
    public void Add_ShouldReturnSum()
    {
      Calculator calculator = new();
      var sum = calculator.Add(1,2).Perf();
      Assert.Equal(3, sum);
    }
}
```

&nbsp;


This will require the following assertions to pass in order for the unit test to succeed, _in this order_ (that way if the test fails on a defined assertion, the benchmark won't run unecessarily):
 - Assert.Equal(3, sum)
 - Assert.True(benchTime < 1 millisecond)
 - Assert.True(memory < 8 bytes)

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
        new BenchmarkConfig() {ExpectedMaxMemoryBytes = 8, ExpectedMaxTimeMs = 1}
      );

      Assert.True(benchTime < 1000000, $"Expected execution to take < 1.00 ms, but took {Format.FormatTime(benchTime, true)}");
      Assert.True(memory < 8, $"Expected execution to use < 8 bytes, but took {Format.FormatMemory(memory, true)}");

    }
}
```
  
</details>

## Important Notes
- **.Perf()**
  - Only one .Perf() call is allowed in a test. 
  - Omitting the `.Perf()` tag in the unit test will cause the _entire_ unit test to be benchmarked. This is probably not what you want, except if your test contains no Arrange or Assert code itself
  - If you have a void method you're testing, you will need to place the `.Perf()` tag higher up in the call chain. For example, `calculator.doVoidWork()` should be tagged as `calculator.Perf().doVoidWork()`.   
  - You _can_ use it in lambda methods, but be careful of scope. Only the immediate call tagged with `.Perf()` will be benchmarked, and it may not have access to surrounding variables. For example, in the following lambda the code will fail as `n` will be out of scope:
    ```csharp
    private void Test8()
    {
        var sum = numbers.Where((n) =>
        {
            calculator.Add(n, n*n).Perf();
            return n % 3 == 0;
            }
        ).Sum(x => (long)x);
    }
    ```
> [!CAUTION]
> **Disable Parallelisation**
> 
> Make sure to add `[assembly: CollectionBehavior(DisableTestParallelization = true)]` somewhere in your test project, or add classes with performance tests to the same xUnit Collection. Running tests in parallel will harm any performance results.

---

## Reason for existence
I was playing around with refactoring huge chunks of a project of mine, and realised in several places I'd actually worsened performance in the process. I had been using `Benchmark.NET` to test several of these changes, but realised I could instead roll these into my unit tests; I wasn't trying to eke out every last drop of performance, but needed to ensure my functions ran within reasonable boundaries (e.g. keeping certain methods allocationless, or making sure LINQ operations weren't taking longer than a few milliseconds). 

I decided to use this as an excuse to dabble with source generation and came up with PerfUnit. 
Of course, halfway through the project I stumbled across `NBench` which seems to be exactly what I needed, if a bit verbose. Ah well. 

