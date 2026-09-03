using System.Xml.Linq;
using FluentAssertions;

namespace Capstone.ArchitectureTests;

/// <summary>
/// Checks the dependency graph as it is written in the .csproj files.
/// </summary>
/// <remarks>
/// This exists alongside <see cref="CompiledReferenceTests"/> because the two
/// catch different mistakes, and either one alone leaves a real gap.
///
/// The compiler drops a project reference from the emitted assembly if no type
/// from it is actually used, so a reflection-only check silently passes on a
/// reference that was declared but not yet used - which is exactly the state a
/// boundary violation is in on the day somebody adds it and before they write
/// the code that needs it. This test reads the intent; the other reads the
/// outcome.
/// </remarks>
public class DeclaredReferenceTests
{
    [Fact]
    public void NoProjectDeclaresAReferenceItIsNotAllowedToHave()
    {
        var violations = new List<string>();

        foreach (var project in CapstoneLayout.SourceProjects())
        {
            var name = Path.GetFileNameWithoutExtension(project);

            ModuleBoundaries.Allowed.Should().ContainKey(name,
                "every project under capstone/src must have its allowed references declared in "
                + "ModuleBoundaries - a new project is a deliberate decision, not a default");

            var allowed = ModuleBoundaries.Allowed[name];

            foreach (var referenced in CapstoneLayout.DeclaredProjectReferences(project))
            {
                if (!allowed.Contains(referenced))
                {
                    violations.Add($"{name} -> {referenced}");
                }
            }
        }

        violations.Should().BeEmpty(
            "these project references are not in the allowed graph. If one of them is "
            + "deliberate, add it to ModuleBoundaries.Allowed and say why in the commit - "
            + "the point of this test is that widening the graph is a decision somebody makes "
            + "on purpose rather than a thing that happens");
    }

    [Fact]
    public void EveryDeclaredProjectIsOneTheRulesKnowAbout()
    {
        var declared = CapstoneLayout.SourceProjects()
            .Select(include => Path.GetFileNameWithoutExtension(include))
            .OfType<string>()
            .ToArray();

        declared.Should().NotBeEmpty("the tests cannot find the capstone source projects at all");

        // Both directions: a rule for a project that no longer exists is a rule
        // nobody is enforcing and everybody assumes is still working.
        ModuleBoundaries.Allowed.Keys.Should().BeSubsetOf(declared);
    }
}

internal static class CapstoneLayout
{
    public static IReadOnlyList<string> SourceProjects()
        => Directory.GetFiles(Path.Combine(Root().FullName, "src"), "*.csproj", SearchOption.AllDirectories);

    public static IReadOnlyList<string> DeclaredProjectReferences(string csprojPath)
        => XDocument.Load(csprojPath)
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .OfType<string>()
            // csproj paths are written with backslashes. On Linux - which is
            // where CI runs - a backslash is a legal filename character, not a
            // separator, so Path.GetFileNameWithoutExtension would hand back
            // the whole relative path and every comparison below would
            // silently pass. Normalising first is what makes this test mean
            // the same thing on both platforms.
            .Select(include => include.Replace('\\', '/'))
            .Select(include => Path.GetFileNameWithoutExtension(include))
            .OfType<string>()
            .ToArray();

    private static DirectoryInfo Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
               && !string.Equals(directory.Name, "capstone", StringComparison.OrdinalIgnoreCase))
        {
            directory = directory.Parent;
        }

        return directory ?? throw new InvalidOperationException(
            $"Could not find the capstone directory above {AppContext.BaseDirectory}. "
            + "These tests read the .csproj files from disk, so they need the source tree, "
            + "not just the compiled output.");
    }
}
