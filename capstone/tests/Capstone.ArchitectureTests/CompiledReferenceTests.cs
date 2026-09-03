using System.Reflection;
using Capstone.Catalog.Contracts;
using Capstone.Catalog.Infrastructure;
using Capstone.Curation.Application.Abstractions;
using Capstone.Curation.Contracts;
using Capstone.Curation.Domain;
using Capstone.Curation.Infrastructure;
using Capstone.SharedKernel;
using Capstone.Sharing.Application;
using Capstone.Sharing.Infrastructure;
using FluentAssertions;

namespace Capstone.ArchitectureTests;

/// <summary>
/// Checks the dependency graph as it actually compiled.
/// </summary>
/// <remarks>
/// The counterpart to <see cref="DeclaredReferenceTests"/>: this one reads what
/// the assemblies really bind to, which catches a dependency arriving by a
/// route the .csproj does not obviously show - a transitive reference that a
/// project has started using directly, for instance.
/// </remarks>
public class CompiledReferenceTests
{
    /// <summary>
    /// One type per assembly, purely to force the loader to bring it in.
    /// Assembly.GetReferencedAssemblies() on a not-yet-loaded assembly is a
    /// test that passes by not looking.
    /// </summary>
    private static IReadOnlyList<Assembly> CapstoneAssemblies() =>
    [
        typeof(AggregateRoot<>).Assembly,
        typeof(IQuoteCatalog).Assembly,
        typeof(InMemoryQuoteCatalog).Assembly,
        typeof(Collection).Assembly,
        typeof(ICollectionRepository).Assembly,
        typeof(CollectionPublishedIntegrationEvent).Assembly,
        typeof(UnitOfWork).Assembly,
        typeof(CollectionPublishedHandler).Assembly,
        typeof(InMemoryFeedWriter).Assembly,
    ];

    [Fact]
    public void NoAssemblyBindsToACapstoneAssemblyItIsNotAllowedTo()
    {
        var violations = new List<string>();

        foreach (var assembly in CapstoneAssemblies())
        {
            var name = assembly.GetName().Name!;
            var allowed = ModuleBoundaries.Allowed[name];

            var actual = assembly.GetReferencedAssemblies()
                .Select(reference => reference.Name!)
                .Where(reference => reference.StartsWith("Capstone.", StringComparison.Ordinal));

            foreach (var reference in actual.Where(reference => !allowed.Contains(reference)))
            {
                violations.Add($"{name} -> {reference}");
            }
        }

        violations.Should().BeEmpty();
    }

    [Fact]
    public void NoModuleBindsToAnotherModulesInfrastructure()
    {
        var violations = new List<string>();

        foreach (var assembly in CapstoneAssemblies())
        {
            var name = assembly.GetName().Name!;

            if (name == ModuleBoundaries.CompositionRoot)
            {
                continue;
            }

            var ownModule = ModulePrefix(name);

            var foreignInfrastructure = assembly.GetReferencedAssemblies()
                .Select(reference => reference.Name!)
                .Where(ModuleBoundaries.IsInfrastructure)
                .Where(reference => ModulePrefix(reference) != ownModule);

            violations.AddRange(foreignInfrastructure.Select(reference => $"{name} -> {reference}"));
        }

        violations.Should().BeEmpty(
            "one module reaching into another's adapters is the specific mistake that turns a "
            + "modular monolith back into a monolith - it couples them to each other's "
            + "storage, not to each other's meaning");
    }

    [Fact]
    public void TheDomainDependsOnNoFrameworkExceptTheRuntime()
    {
        var forbidden = new[]
        {
            "Microsoft.EntityFrameworkCore",
            "Microsoft.AspNetCore",
            "Dapper",
            "Azure.Messaging",
            "Polly",
        };

        foreach (var assembly in CapstoneAssemblies().Where(a => ModuleBoundaries.IsDomain(a.GetName().Name!)))
        {
            var references = assembly.GetReferencedAssemblies().Select(reference => reference.Name!);

            foreach (var reference in references)
            {
                forbidden.Should().NotContain(
                    prefix => reference.StartsWith(prefix, StringComparison.Ordinal),
                    "the domain has to stay testable without a database, a host, or a broker - "
                    + $"but {assembly.GetName().Name} references {reference}");
            }
        }
    }

    [Fact]
    public void ContractsCarryNoDependencyOnTheModuleThatPublishesThem()
    {
        var contracts = CapstoneAssemblies()
            .Where(assembly => assembly.GetName().Name!.EndsWith(".Contracts", StringComparison.Ordinal));

        foreach (var assembly in contracts)
        {
            assembly.GetReferencedAssemblies()
                .Select(reference => reference.Name!)
                .Where(reference => reference.StartsWith("Capstone.", StringComparison.Ordinal))
                .Should().BeEmpty(
                    "a subscriber must be able to reference a contract without compiling "
                    + "against the publisher's internals; if it cannot, the contract is not a "
                    + "boundary, it is a header file");
        }
    }

    private static string ModulePrefix(string assemblyName)
    {
        // "Capstone.Curation.Infrastructure" -> "Curation"
        var parts = assemblyName.Split('.');
        return parts.Length >= 2 ? parts[1] : assemblyName;
    }
}
