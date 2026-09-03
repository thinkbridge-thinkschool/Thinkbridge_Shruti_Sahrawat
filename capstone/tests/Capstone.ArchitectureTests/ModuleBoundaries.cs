namespace Capstone.ArchitectureTests;

/// <summary>
/// The dependency graph this solution is allowed to have, written down once.
/// </summary>
/// <remarks>
/// A modular monolith is only modular while something stops one module reaching
/// into another. Folders do not stop anyone - the existing QuotesApi is layered
/// by folder inside a single assembly, and nothing there prevents Domain from
/// referencing Data except that nobody has done it yet. Project references make
/// the boundary real, and this table makes it reviewable: adding a reference
/// that is not listed here fails the build, so the conversation about whether a
/// module should be allowed to see another one happens at the point somebody
/// tries it.
/// </remarks>
public static class ModuleBoundaries
{
    public const string CompositionRoot = "Capstone.Api";

    /// <summary>
    /// For each assembly, the Capstone assemblies it may reference. Anything
    /// not listed is a violation.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> Allowed =
        new Dictionary<string, string[]>
        {
            // The shared kernel is shared precisely because it depends on
            // nothing. The moment it references a module, every module
            // transitively depends on that one.
            ["Capstone.SharedKernel"] = [],

            // Contracts are the published language. They must be referenceable
            // without dragging in the module that publishes them - otherwise
            // subscribing to Curation means compiling against Curation's
            // aggregate, and the boundary is decorative.
            ["Capstone.Catalog.Contracts"] = [],
            ["Capstone.Curation.Contracts"] = [],

            // The core domain: shared kernel only. No persistence, no
            // messaging, no other module.
            ["Capstone.Curation.Domain"] = ["Capstone.SharedKernel"],

            // Use cases: own domain, plus other modules' contracts. Never
            // another module's internals, never any infrastructure.
            ["Capstone.Curation.Application"] =
            [
                "Capstone.SharedKernel",
                "Capstone.Curation.Domain",
                "Capstone.Catalog.Contracts",
            ],

            // Adapters: may see their own module entirely, including the
            // contracts they translate outbound events into.
            ["Capstone.Curation.Infrastructure"] =
            [
                "Capstone.SharedKernel",
                "Capstone.Curation.Domain",
                "Capstone.Curation.Application",
                "Capstone.Curation.Contracts",
            ],

            ["Capstone.Catalog.Infrastructure"] = ["Capstone.Catalog.Contracts"],

            // Sharing subscribes to Curation through the contract and nothing
            // else. This single entry is the whole reason the two modules can
            // be developed and deployed independently later.
            ["Capstone.Sharing.Application"] = ["Capstone.Curation.Contracts"],

            ["Capstone.Sharing.Infrastructure"] = ["Capstone.Sharing.Application"],

            // The composition root is the one place allowed to know everything,
            // because somebody has to bolt the modules together and it should
            // be exactly one somebody.
            [CompositionRoot] =
            [
                "Capstone.SharedKernel",
                "Capstone.Catalog.Contracts",
                "Capstone.Catalog.Infrastructure",
                "Capstone.Curation.Contracts",
                "Capstone.Curation.Domain",
                "Capstone.Curation.Application",
                "Capstone.Curation.Infrastructure",
                "Capstone.Sharing.Application",
                "Capstone.Sharing.Infrastructure",
            ],
        };

    public static bool IsInfrastructure(string assemblyName)
        => assemblyName.EndsWith(".Infrastructure", StringComparison.Ordinal);

    public static bool IsDomain(string assemblyName)
        => assemblyName.EndsWith(".Domain", StringComparison.Ordinal);
}
