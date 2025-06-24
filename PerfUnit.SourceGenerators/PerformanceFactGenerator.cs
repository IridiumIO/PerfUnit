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


            // Generate one file per class group
            foreach (var classGroup in classGroups)
            {
                var ns = classGroup.Key.Namespace;
                var className = classGroup.Key.ClassName;
                var methodList = classGroup.Value;

                var methodsBuilder = new StringBuilder();


                HashSet<string> usings = ["using System;", "using Xunit;", "using PerfUnit;", "using PerfUnit.SharedStandard;"];

                foreach (var (method, semanticModel) in methodList)
                {

                    var root = method.SyntaxTree.GetRoot();
                    var usingDirectives = root.DescendantNodes()
                        .OfType<UsingDirectiveSyntax>();
                    foreach (var usingDirective in usingDirectives)
                    {
                        usings.Add(usingDirective.ToFullString().Trim());
                    }



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

                    //Get other attributes
                    var attributeLines = new StringBuilder();
                    foreach (var attrList in method.AttributeLists)
                    {
                        foreach (var attr in attrList.Attributes)
                        {
                            var attrName = attr.Name.ToString();
                            if (!attrName.Contains("Fact") && !attrName.Contains("PerfSpeed") && !attrName.Contains("PerfMemory")) // filter out if needed
                            {
                                attributeLines.AppendLine($"[{attr}]");
                            }
                        }
                    }

                    var methodBody = new StringBuilder();

                    string expressionline = String.Empty;

                    foreach (var statement in bodyStatements)
                    {

                        // Find all invocation expressions in the statement
                        var invocations = statement.DescendantNodesAndSelf()
                            .OfType<InvocationExpressionSyntax>();

                        foreach (var invocation in invocations)
                        {
                            


                            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                                 memberAccess.Name.Identifier.Text == "Perf")
                            {
                                // Find the outermost invocation/member access containing this .Perf() call
                                SyntaxNode outer = invocation;
                                while ( outer.Parent is InvocationExpressionSyntax || outer.Parent is MemberAccessExpressionSyntax)
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
                                    expressionline = cleanedExpr;
                                }
                                else
                                {
                                    expressionline = "var _dis_ = " + cleanedExpr;
                                }
                                break;
                            }


                        }

                        methodBody.Append(statement.ToFullString().Replace(".Perf()", ""));

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
                                                        {{attributeLines}}
                                                        [Fact(DisplayName = "{{methodName}}")]
                                                        public void {{methodName}}_g() {
                                                            {{methodBody}}

                                                            var (benchTime, memory) = SimpleBenchmarker.Run(() =>
                                                            {
                                                                {{expressionline}};
                                                            }, 
                                                            expectedMaxMemoryBytes:{{(memoryAndComparer.HasValue ? memoryAndComparer.Value.Item1 : 0)}}, 
                                                            expectedMaxTimeMs: {{(timeAndComparer.HasValue ? timeAndComparer.Value.Item1 / 1_000_000.0 : 0)}}
                                                            );
                                                        
                                                            {{timeAndComparerInlineTest}}
                                                            {{memoryAndComparerInlineTest}}
                                                        }

                                                """);
                }


                // Build usings string
                var usingsBuilder = new StringBuilder();
                foreach (var usingLine in usings)
                {
                    usingsBuilder.AppendLine(usingLine);
                }

                var generatedCode = $$"""
                                    {{usingsBuilder}}

                                    namespace {{ns}} {

                                        public partial class {{className}} {
                                            {{methodsBuilder}}
                                        }
                                    }
                                    """;

                spc.AddSource($"{className}.g.cs", SourceText.From(generatedCode, Encoding.UTF8));
            }
        });
    }



    string RemovePerfFromChain(ExpressionSyntax expr)
    {
        // If this is an invocation of .Perf(), replace it with its receiver
        if (expr is InvocationExpressionSyntax inv &&
            inv.Expression is MemberAccessExpressionSyntax member &&
            member.Name.Identifier.Text == "Perf")
        {
            // Replace .Perf() with its receiver, but keep any further member accesses or invocations
            return RemovePerfFromChain(member.Expression);
        }
        // If this is an invocation (e.g., .HeavyLinq()), reconstruct it
        else if (expr is InvocationExpressionSyntax inv2)
        {
            var exprPart = RemovePerfFromChain(inv2.Expression);
            return exprPart + inv2.ArgumentList.ToString();
        }
        // If this is a member access (e.g., .HeavyLinq), reconstruct it
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
                SizeUnit.Kilobytes => Math.Round(value * 1_000.0,4),
                SizeUnit.Megabytes => Math.Round(value * 1_000_000.0, 4),
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
