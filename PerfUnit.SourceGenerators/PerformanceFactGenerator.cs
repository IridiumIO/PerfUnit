using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using PerfUnit.SharedStandard;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace PerfUnit.SourceGenerators;

[Generator]
public class PerformanceFactGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var methodDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: IsPerformanceFactMethod,
                transform: TransformToMethodTuple)
            .Collect();

        context.RegisterSourceOutput(methodDeclarations, GeneratePerformanceFactClasses);
    }


    private static bool IsPerformanceFactMethod(SyntaxNode node, CancellationToken _)
        => node is MethodDeclarationSyntax method &&
           method.AttributeLists.ToString().Contains("PerformanceFact");


    private static (MethodDeclarationSyntax Method, SemanticModel SemanticModel) TransformToMethodTuple(GeneratorSyntaxContext ctx, CancellationToken _)
        => ((MethodDeclarationSyntax)ctx.Node, ctx.SemanticModel);


    private void GeneratePerformanceFactClasses(SourceProductionContext context, ImmutableArray<(MethodDeclarationSyntax Method, SemanticModel SemanticModel)> methodTuples)
    {
        var classGroups = GroupMethodsByClass(methodTuples);

        foreach (var classGroup in classGroups)
        {
            var ns = classGroup.Key.Namespace;
            var className = classGroup.Key.ClassName;
            var methodList = classGroup.Value;

            var usings = CollectUsings(methodList);
            var methodsBuilder = new StringBuilder();

            foreach (var (method, semanticModel) in methodList)
            {
                if (!TryGeneratePerformanceFactMethod(context, method, semanticModel, out var methodCode))
                    continue;

                methodsBuilder.AppendLine(methodCode);
            }

            var usingsBuilder = new StringBuilder();
            foreach (var usingLine in usings)
                usingsBuilder.AppendLine(usingLine);

            var generatedCode = $$"""
                {{usingsBuilder}}

                namespace {{ns}} {
                    
                    [Collection("PerfUnit.SequentialTests")]
                    public partial class {{className}} {
                        {{methodsBuilder}}
                    }
                }
                """;

            context.AddSource($"{className}.g.cs", SourceText.From(generatedCode, Encoding.UTF8));
        }
    }

    private static Dictionary<(string Namespace, string ClassName), List<(MethodDeclarationSyntax, SemanticModel)>> GroupMethodsByClass(
        ImmutableArray<(MethodDeclarationSyntax Method, SemanticModel SemanticModel)> methodTuples)
    {
        var classGroups = new Dictionary<(string Namespace, string ClassName), List<(MethodDeclarationSyntax, SemanticModel)>>();

        foreach (var (method, semanticModel) in methodTuples)
        {
            var classDecl = method.Parent as ClassDeclarationSyntax;
            if (classDecl == null) continue;

            var ns = GetNamespace(classDecl);
            var className = classDecl.Identifier.Text;
            var key = (ns, className);

            if (!classGroups.TryGetValue(key, out var list))
            {
                list = new List<(MethodDeclarationSyntax, SemanticModel)>();
                classGroups[key] = list;
            }
            list.Add((method, semanticModel));
        }

        return classGroups;
    }

    private static HashSet<string> CollectUsings(List<(MethodDeclarationSyntax, SemanticModel)> methodList)
    {
        var usings = new HashSet<string>
        {
            "using System;",
            "using Xunit;",
            "using PerfUnit;",
            "using PerfUnit.SharedStandard;"
        };

        foreach (var (method, _) in methodList)
        {
            var root = method.SyntaxTree.GetRoot();
            var usingDirectives = root.DescendantNodes()
                .OfType<UsingDirectiveSyntax>();
            foreach (var usingDirective in usingDirectives)
            {
                usings.Add(usingDirective.ToFullString().Trim());
            }
        }
        return usings;
    }

    private bool TryGeneratePerformanceFactMethod(SourceProductionContext ctx, MethodDeclarationSyntax method, SemanticModel semanticModel, out string methodCode)
    {
        methodCode = string.Empty;
        int perfCallCount = CountPerfCalls(method);

        if (perfCallCount > 1)
        {
            ReportDiagnostic(ctx, MultipleActsRule, semanticModel, method, method.Identifier.Text);
            return false;
        }

        var attributeLines = GetNonFactAttributes(method);
        var methodBody = GetMethodBodyWithoutPerf(method);
        var expressionLine = GetPerfExpressionLine(method, semanticModel);

        var timeAndComparer = GetExpectedNanoSecondsAndComparer(method, semanticModel);
        var timeCheck = GetTimeCheckInlineTest(timeAndComparer);

        var memoryAndComparer = GetExpectedMemoryAndComparer(method, semanticModel);
        var memoryCheck = GetMemoryCheckInlineTest(memoryAndComparer);

        if (!(timeAndComparer.HasValue || memoryAndComparer.HasValue))
        {
            ReportDiagnostic(ctx, NoPerformanceCheckRule, semanticModel, method, method.Identifier.Text);
        }

        methodCode = $$"""
            {{attributeLines}}
            [Fact(DisplayName = "{{method.Identifier.Text}}")]
            public void {{method.Identifier.Text}}_g() {
                {{methodBody}}

                var (benchTime, memory) = SimpleBenchmarker.Run(() =>
                {
                    {{expressionLine}};
                }, 
                new BenchmarkConfig() {ExpectedMaxMemoryBytes = {{(memoryAndComparer.HasValue ? memoryAndComparer.Value.Item1 : -1)}}, ExpectedMaxTimeMs = {{(timeAndComparer.HasValue ? timeAndComparer.Value.Item1 / 1_000_000.0 : -1)}}}
                );
            
                {{timeCheck}}
                {{memoryCheck}}
            }
        """;
        return true;
    }

    //expectedMaxMemoryBytes:{{(memoryAndComparer.HasValue? memoryAndComparer.Value.Item1 : -1)}}, 
    //            expectedMaxTimeMs: { { (timeAndComparer.HasValue ? timeAndComparer.Value.Item1 / 1_000_000.0 : -1)} }

private static int CountPerfCalls(MethodDeclarationSyntax method)
    {

        var body = method.Body?.ToFullString();
        const string perfMarker = ".Perf()";

        int count = 0;

        for (var i = 0; i < body?.Length - perfMarker.Length; i++)
        {
            if (body.Substring(i, perfMarker.Length) == perfMarker)
            {
                count++;
                i += perfMarker.Length - 1; 
            }
        }

        return count;
    }

    private static string GetNonFactAttributes(MethodDeclarationSyntax method)
    {
        var attributeLines = new StringBuilder();
        foreach (var attrList in method.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var attrName = attr.Name.ToString();
                if (!attrName.Contains("Fact") && !attrName.Contains("PerfSpeed") && !attrName.Contains("PerfMemory"))
                {
                    attributeLines.AppendLine($"[{attr}]");
                }
            }
        }
        return attributeLines.ToString();
    }

    private static string GetMethodBodyWithoutPerf(MethodDeclarationSyntax method)
    {
        var methodBody = new StringBuilder();
        var bodyStatements = method.Body?.Statements ?? default;
        foreach (var statement in bodyStatements)
        {
            methodBody.Append(statement.ToFullString().Replace(".Perf()", ""));
        }
        return methodBody.ToString();
    }

    private static string GetPerfExpressionLine(MethodDeclarationSyntax method, SemanticModel semanticModel)
    {
        var bodyStatements = method.Body?.Statements ?? default;
        foreach (var statement in bodyStatements)
        {
            var invocations = statement.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>();

            foreach (var invocation in invocations)
            {
                if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                    memberAccess.Name.Identifier.Text == "Perf")
                {
                    // Find the outermost invocation/member access containing this .Perf() call
                    SyntaxNode outer = invocation;
                    while (outer.Parent is InvocationExpressionSyntax || outer.Parent is MemberAccessExpressionSyntax)
                    {
                        outer = outer.Parent;
                    }

                    // Remove .Perf() from the outermost expression
                    var cleanedExpr = RemovePerfFromChain((ExpressionSyntax)outer);

                    // Use semantic model to get the symbol and return type for the cleaned, outermost invocation
                    var symbolInfo = semanticModel.GetSymbolInfo(outer);
                    var methodSymbol = symbolInfo.Symbol as IMethodSymbol;
                    ITypeSymbol returnType = methodSymbol?.ReturnType;

                    if (returnType != null && returnType.SpecialType == SpecialType.System_Void)
                    {
                        return cleanedExpr;
                    }
                    else
                    {
                        return "var _dis_ = " + cleanedExpr;
                    }
                }
            }
        }

        //If no .Perf() call found, return the entire method body as a fallback
        if (bodyStatements.Count > 0) return string.Concat(bodyStatements.Select(s => s.ToFullString()));

        return string.Empty;
    }

    private static string GetTimeCheckInlineTest((double, MustTake)? timeAndComparer)
    {
        if (!timeAndComparer.HasValue) return string.Empty;
        var (time, comparer) = timeAndComparer.Value;
        var op = comparer == MustTake.LessThan ? "<" : "<=";

        var errorMessage = $"Expected execution to take {op} {Format.FormatTime(time, true)}, but took {{Format.FormatTime(benchTime, true)}}";

        return $$"""
            Assert.True(benchTime {{op}} {{time}}, $"{{errorMessage}}");
            """;
    }

    private static string GetMemoryCheckInlineTest((double, MustUse)? memoryAndComparer)
    {
        if (!memoryAndComparer.HasValue) return string.Empty;
        var (memory, comparer) = memoryAndComparer.Value;
        var op = comparer == MustUse.LessThan ? "<" : "<=";

        var errorMessage = $"Expected memory usage to be {op} {{memory}} bytes, but used {memory} bytes";

        return $$"""
            Assert.True(memory {{op}} {{memory}}, $"{{errorMessage}}");
            """;
    }

    private static void ReportDiagnostic(SourceProductionContext ctx, DiagnosticDescriptor rule, SemanticModel semanticModel, MethodDeclarationSyntax method, string methodName)
    {
        var diagnostic = Diagnostic.Create(rule, method.GetLocation(),methodName);
        ctx.ReportDiagnostic(diagnostic);
    }


    static string RemovePerfFromChain(ExpressionSyntax expr)
    {
        if (expr is InvocationExpressionSyntax inv &&
            inv.Expression is MemberAccessExpressionSyntax member &&
            member.Name.Identifier.Text == "Perf")
        {
            return RemovePerfFromChain(member.Expression);
        }
        else if (expr is InvocationExpressionSyntax inv2)
        {
            var exprPart = RemovePerfFromChain(inv2.Expression);
            return exprPart + inv2.ArgumentList.ToString();
        }
        else if (expr is MemberAccessExpressionSyntax member2)
        {
            var exprPart = RemovePerfFromChain(member2.Expression);
            return exprPart + "." + member2.Name.Identifier.Text;
        }
        else
        {
            return expr.ToString();
        }
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
            var comparison = perfSpeedAttr.ConstructorArguments[0].Value is int cmpVal
                ? (MustTake)cmpVal
                : MustTake.LessThan;
            var value = perfSpeedAttr.ConstructorArguments[1].Value is double dVal
                ? dVal
                : 200.0;
            var unit = perfSpeedAttr.ConstructorArguments.Length > 2 && perfSpeedAttr.ConstructorArguments[2].Value is int unitVal
                ? (TimeUnit)unitVal
                : TimeUnit.Milliseconds;

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
            var comparison = perfMemAttr.ConstructorArguments[0].Value is int cmpVal
                ? (MustUse)cmpVal
                : MustUse.LessThan;
            var value = perfMemAttr.ConstructorArguments[1].Value is double dVal
                ? dVal
                : 100.0;
            var unit = perfMemAttr.ConstructorArguments.Length > 2 && perfMemAttr.ConstructorArguments[2].Value is int unitVal
                ? (SizeUnit)unitVal
                : SizeUnit.Bytes;

            double bytes = unit switch
            {
                SizeUnit.Bytes => value,
                SizeUnit.Kilobytes => Math.Round(value * 1_000.0, 4),
                SizeUnit.Megabytes => Math.Round(value * 1_000_000.0, 4),
                _ => throw new InvalidOperationException("Invalid size unit")
            };

            return (bytes, comparison);
        }

        return null;
    }

    private static string GetNamespace(ClassDeclarationSyntax classDeclarationSyntax)
    {
        var namespaceNode = classDeclarationSyntax.FirstAncestorOrSelf<NamespaceDeclarationSyntax>();
        if (namespaceNode != null)
            return namespaceNode.Name.ToString();

        var fileScopedNamespace = classDeclarationSyntax.FirstAncestorOrSelf<FileScopedNamespaceDeclarationSyntax>();
        if (fileScopedNamespace != null)
            return fileScopedNamespace.Name.ToString();

        return string.Empty;
    }

    private static readonly DiagnosticDescriptor MultipleActsRule = new(
        id: "PERF001",
        title: "Multiple .Perf() calls are not allowed",
        messageFormat: "Method '{0}' contains more than one .Perf() call",
        category: "Usage",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NoPerformanceCheckRule = new(
       id: "PERF002",
       title: "No Performance Checks used",
       messageFormat: "Method '{0}' does not contain any performance checks. Benchmarking will run, but no memory or speed comparisons will occur.",
       category: "Usage",
       DiagnosticSeverity.Warning,
       isEnabledByDefault: true);

}