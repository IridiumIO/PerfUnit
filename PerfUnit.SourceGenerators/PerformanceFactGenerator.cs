using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Text;
using PerfUnit.SharedStandard;

namespace PerfUnit.SourceGenerators;

[Generator]
public class PerformanceFactGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var methodDeclarations = context.SyntaxProvider
        .CreateSyntaxProvider(
            predicate: (s, _) => s is MethodDeclarationSyntax method &&
                                 method.AttributeLists.ToString().Contains("PerformanceFact"),
            transform: (ctx, _) => (
                Method: (MethodDeclarationSyntax)ctx.Node,
                SemanticModel: ctx.SemanticModel
            ))
        .Collect();

        context.RegisterSourceOutput(methodDeclarations, (spc, methodTuples) =>
        {

            // Group methods by (namespace, className)
            var classGroups = new Dictionary<(string Namespace, string ClassName), List<MethodDeclarationSyntax>>();

            foreach (var (method, semanticModel) in methodTuples)
            {
                var classDecl = method.Parent as ClassDeclarationSyntax;
                if (classDecl == null) continue;

                var ns = GetNamespace(classDecl);
                var className = classDecl.Identifier.Text;
                var key = (ns, className);

                if (!classGroups.TryGetValue(key, out var list))
                {
                    list = new List<MethodDeclarationSyntax>();
                    classGroups[key] = list;
                }
                list.Add(method);
            }


            // Generate one file per class group
            foreach (var classGroup in classGroups)
            {
                var ns = classGroup.Key.Namespace;
                var className = classGroup.Key.ClassName;
                var methodList = classGroup.Value;

                var methodsBuilder = new StringBuilder();


                foreach (var (method, semanticModel) in methodTuples)
                {
                    int actCount = 0;
                    var methodName = method.Identifier.Text;

                    // Get only the statements inside the method body (no braces)
                    var bodyStatements = method.Body?.Statements ?? default;

                    foreach (var statement in bodyStatements)
                    {
                        var line = statement.ToFullString();
                        if (line.Contains(".Perf()"))
                            actCount++;
                    }

                    if (actCount > 1)
                    {
                        // Report a warning diagnostic at the method location
                        var diagnostic = Diagnostic.Create(
                            MultipleActsRule,
                            method.GetLocation(),
                            methodName);
                        spc.ReportDiagnostic(diagnostic);
                        // Optionally, skip code generation for this method
                        continue;
                    }

                    var methodBody = new StringBuilder();

                    string expressionline = String.Empty;

                    foreach (var statement in bodyStatements)
                    {
                        var line = statement.ToFullString();
                        if (line.Contains(".Perf()"))
                        {
                            expressionline = line.Replace(".Perf()", "").TrimEnd();

                            methodBody.Append(expressionline);

                        }
                        else
                        {
                            methodBody.AppendLine(line.TrimEnd());
                        }
                    }

                    var timeAndComparer = GetExpectedNanoSecondsAndComparer(method, semanticModel);
                    string timeAndComparerInlineTest = timeAndComparer.HasValue
                        ? $$"""
                            if (benchTime {{(timeAndComparer.Value.Item2 == MustTake.LessThan ? ">=" : ">")}} {{timeAndComparer.Value.Item1}})
                                        throw new Xunit.Sdk.XunitException($"Expected execution to be under {{PrettyTime.FormatTime(timeAndComparer.Value.Item1)}}, but took {PrettyTime.FormatTime(benchTime)}");
                        
                            """        
                        : string.Empty;

                    var memoryAndComparer = GetExpectedMemoryAndComparer(method, semanticModel);
                    string memoryAndComparerInlineTest = memoryAndComparer.HasValue
                        ? $$"""
                            if (memory {{(memoryAndComparer.Value.Item2 == MustUse.LessThan ? ">=" : ">")}} {{memoryAndComparer.Value.Item1}})
                                        throw new Xunit.Sdk.XunitException($"Expected memory usage to be under {{memoryAndComparer.Value.Item1}} bytes, but used {memory} bytes");
                        
                            """        
                        : string.Empty;

                    if (!(timeAndComparer.HasValue || memoryAndComparer.HasValue))
                    {
                        var diagnostic = Diagnostic.Create(
                            NoPerformanceCheckRule,
                            method.GetLocation(),
                            methodName);
                        spc.ReportDiagnostic(diagnostic);
                    }

                    methodsBuilder.AppendLine($$"""
                                                    [Fact(DisplayName = "{{methodName}}")]
                                                    public void {{methodName}}_g() {
                                                        {{methodBody}}

                                                        var (benchTime, memory) = SimpleBenchmarker.Run(() =>
                                                        {
                                                            {{expressionline}}
                                                        }
                                                        );
                                                        
                                                        {{timeAndComparerInlineTest}}
                                                        {{memoryAndComparerInlineTest}}
                                                    }

                                                """);
                }

                var generatedCode = $$"""
                                        using System;
                                        using Xunit;
                                        using System.Diagnostics;
                                        using PerfUnit;
                                        using PerfUnit.SharedStandard;

                                        namespace {{ns}};

                                        public partial class {{className}} {
                                        {{methodsBuilder}}
                                        }
                                        """;

                spc.AddSource($"{className}.g.cs", SourceText.From(generatedCode, Encoding.UTF8));
            }
        });
    }



    private (double, MustTake)? GetExpectedNanoSecondsAndComparer(MethodDeclarationSyntax method, SemanticModel semanticModel)
    {
        var methodSymbol = semanticModel.GetDeclaredSymbol(method);
        if (methodSymbol == null)
            return null;

        var perfSpeedAttr = methodSymbol.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name == "PerfSpeedAttribute" || a.AttributeClass?.ToDisplayString() == "PerfUnit.SharedStandard.PerfSpeedAttribute");

        if (perfSpeedAttr != null)
        {
            // Constructor: PerfSpeedAttribute(MustTake comparison, double value, TimeUnit unit = TimeUnit.Milliseconds)
            var comparison = perfSpeedAttr.ConstructorArguments[0].Value is int cmpVal
                ? (MustTake)cmpVal
                : MustTake.LessThan;
            var value = perfSpeedAttr.ConstructorArguments[1].Value is double dVal
                ? dVal
                : 200.0;
            var unit = perfSpeedAttr.ConstructorArguments.Length > 2 && perfSpeedAttr.ConstructorArguments[2].Value is int unitVal
                ? (TimeUnit)unitVal
                : TimeUnit.Milliseconds;

            // Convert to nanoseconds
            double nanoseconds = unit switch
            {
                TimeUnit.Nanoseconds => value,
                TimeUnit.Microseconds => value * 1_000.0,
                TimeUnit.Milliseconds => value * 1_000_000.0,
                TimeUnit.Seconds => value * 1_000_000_000.0,
                TimeUnit.Minutes => value * 60_000_000_000_000.0,
                _ => throw new InvalidOperationException("Invalid time unit")
            };

            return (nanoseconds, comparison);
        }

        return null;
    }

    private (double, MustUse)? GetExpectedMemoryAndComparer(MethodDeclarationSyntax method, SemanticModel semanticModel)
    {
        var methodSymbol = semanticModel.GetDeclaredSymbol(method);
        if (methodSymbol == null)
            return null;

        var perfMemAttr = methodSymbol.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name == "PerfMemoryAttribute" || a.AttributeClass?.ToDisplayString() == "PerfUnit.SharedStandard.PerfMemoryAttribute");

        if (perfMemAttr != null)
        {
            // Constructor: PerfSpeedAttribute(MustTake comparison, double value, TimeUnit unit = TimeUnit.Milliseconds)
            var comparison = perfMemAttr.ConstructorArguments[0].Value is int cmpVal
                ? (MustUse)cmpVal
                : MustUse.LessThan;
            var value = perfMemAttr.ConstructorArguments[1].Value is double dVal
                ? dVal
                : 100.0;
            var unit = perfMemAttr.ConstructorArguments.Length > 2 && perfMemAttr.ConstructorArguments[2].Value is int unitVal
                ? (SizeUnit)unitVal
                : SizeUnit.Bytes;

            // Convert to nanoseconds
            double bytes = unit switch
            {
                SizeUnit.Bytes => value,
                SizeUnit.Kilobytes => value * 1_000.0,
                SizeUnit.Megabytes => value * 1_000_000.0,
                _ => throw new InvalidOperationException("Invalid size unit")
            };

            return (bytes, comparison);
        }

        return null;
    }


    private static string GetNamespace(ClassDeclarationSyntax classDeclarationSyntax)
    {
        // Check for block-scoped namespace
        var namespaceNode = classDeclarationSyntax.FirstAncestorOrSelf<NamespaceDeclarationSyntax>();
        if (namespaceNode != null)
            return namespaceNode.Name.ToString();

        // Check for file-scoped namespace (C# 10+)
        var fileScopedNamespace = classDeclarationSyntax.FirstAncestorOrSelf<FileScopedNamespaceDeclarationSyntax>();
        if (fileScopedNamespace != null)
            return fileScopedNamespace.Name.ToString();

        // No namespace found (global namespace)
        return string.Empty;
    }


    private static readonly DiagnosticDescriptor MultipleActsRule = new(
        id: "PERF001",
        title: "Multiple .Perf() calls are not allowed",
        messageFormat: "Method '{0}' contains more than one .Perf() call.",
        category: "Usage",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NoPerformanceCheckRule = new(
       id: "PERF002",
       title: "No Performance Checks used",
       messageFormat: "Method '{0}' does not contain any performance checks\nBenchmarking will run, but no memory or speed comparisons will occur",
       category: "Usage",
       DiagnosticSeverity.Warning,
       isEnabledByDefault: true);

}
