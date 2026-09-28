using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Scenarios;

/// <summary>
/// Keeps <c>docs/commissioning.md</c> and the scenarios in step (#31). Every check (C1-C15) has an automated scenario
/// that CI runs. Every scenario the document names exists and names the same check and step. Every step a scenario
/// names is in the document. Every check lists its installation assumptions. An in-process scenario names its check
/// with <see cref="CommissioningCheckAttribute"/>; a container scenario names it with a
/// <c># CommissioningCheck("C12", "3")</c> line in <c>tests/emulator/deploy-scenarios.sh</c>.
/// </summary>
[TestClass]
public class ScenarioCoverageTests
{
    private const string AssumptionOnly = "Installation assumption";
    private const string NoStep = "—";
    private const string ScriptName = "deploy-scenarios.sh";
    private const string StepTableHeader = "| Step | Checked | Scenario |";
    private const string AssumptionsHeading = "**Installation assumptions**";

    private static readonly string[] Checks = [.. Enumerable.Range(1, 15).Select(n => $"C{n}")];
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ProductionOptions.AppSettingsPath)!, "..", ".."));
    private static readonly Lazy<IReadOnlyList<CheckSection>> Document = new(ReadDocument);
    private static readonly Lazy<IReadOnlyList<Coverage>> Attributes = new(ReadAttributes);
    private static readonly Lazy<ContainerScript> Script = new(ReadScript);

    [TestMethod]
    public void TheDocument_HasEveryCheck_InOrder()
    {
        Document.Value.Select(s => s.Check).Should().Equal(Checks);
    }

    [TestMethod]
    public void EveryCheck_HasAScenario_AndItsInstallationAssumptions_EachTiedToASetting()
    {
        foreach (var section in Document.Value)
        {
            section.Rows.SelectMany(r => r.Scenarios).Should().NotBeEmpty("{0} needs an automated scenario", section.Check);
            section.Assumptions.Should().NotBeEmpty("{0} lists its installation assumptions", section.Check);
            section.Assumptions.Should().AllSatisfy(a => a.Should().MatchRegex("depends? on it",
                "each of {0}'s assumptions names the setting that depends on it, or says that none does", section.Check));
        }
    }

    [TestMethod]
    public void EveryStepRow_IsNumberedInOrder_AndNamesScenarios_OrIsAnInstallationAssumption()
    {
        foreach (var section in Document.Value)
        {
            var numbered = section.Rows.TakeWhile(r => r.Step != NoStep).Select(r => r.Step).ToList();
            numbered.Should().Equal(Enumerable.Range(1, numbered.Count).Select(n => n.ToString(CultureInfo.InvariantCulture)),
                "{0}'s steps are numbered from 1, before its {1} rows", section.Check, NoStep);
            section.Rows.Skip(numbered.Count).Should().AllSatisfy(r => r.Step.Should().Be(NoStep));

            foreach (var row in section.Rows)
            {
                row.Checked.Should().NotBeNullOrWhiteSpace("{0} step {1} says what it checks", section.Check, row.Step);
                if (row.Scenarios.Count == 0)
                {
                    row.ScenarioCell.Should().Be(AssumptionOnly, "{0} step {1} names scenarios or is an installation assumption", section.Check, row.Step);
                    row.Step.Should().NotBe(NoStep, "a {0} row lists scenarios beyond the numbered steps", NoStep);
                }
                else
                {
                    row.ScenarioCell.Should().Be(string.Join(", ", row.Scenarios.Select(s => $"`{s}`")),
                        "{0} step {1} lists only its scenarios, each in backticks", section.Check, row.Step);
                }
            }
        }
    }

    [TestMethod]
    public void EveryScenarioTheDocumentNames_ExistsAndNamesThatCheckAndStep()
    {
        var known = Attributes.Value.Concat(Script.Value.Markers).ToHashSet();
        var scenarios = known.Select(c => c.Scenario).ToHashSet(StringComparer.Ordinal);
        var failures = new List<string>();
        foreach (var section in Document.Value)
        {
            foreach (var row in section.Rows)
            {
                var step = row.Step == NoStep ? null : row.Step;
                foreach (var scenario in row.Scenarios)
                {
                    if (!scenarios.Contains(scenario) && !Exists(scenario))
                    {
                        failures.Add($"{section.Check} step {row.Step}: {scenario} does not exist");
                    }
                    else if (!known.Contains(new Coverage(section.Check, step, scenario)))
                    {
                        failures.Add($"{section.Check} step {row.Step}: {scenario} does not name it ({Marker(section.Check, step)})");
                    }
                }
            }
        }

        failures.Should().BeEmpty();
    }

    [TestMethod]
    public void EveryCheckAndStepAScenarioNames_IsInTheDocument()
    {
        var documented = Document.Value
            .SelectMany(s => s.Rows.SelectMany(r => r.Scenarios.Select(scenario => new Coverage(s.Check, r.Step == NoStep ? null : r.Step, scenario))))
            .ToHashSet();

        Attributes.Value.Concat(Script.Value.Markers).Where(c => !documented.Contains(c))
            .Select(c => $"{c.Scenario} names {Marker(c.Check, c.Step)}, which docs/commissioning.md does not list for it")
            .Should().BeEmpty();
    }

    [TestMethod]
    public void EveryScenario_RunsInTheScenariosWorkflow()
    {
        var failures = new List<string>();
        foreach (var method in AttributedMethods())
        {
            var categories = method.GetCustomAttributes<TestCategoryBaseAttribute>(inherit: true)
                .Concat(method.DeclaringType!.GetCustomAttributes<TestCategoryBaseAttribute>(inherit: true))
                .SelectMany(c => c.TestCategories)
                .ToList();
            if (!method.IsDefined(typeof(TestMethodAttribute), inherit: true))
            {
                failures.Add($"{Name(method)} is not a test method");
            }

            if (!categories.Contains(Scenario.Category) && !categories.Contains(Scenario.BrowserCategory))
            {
                failures.Add($"{Name(method)} is in neither the {Scenario.Category} nor the {Scenario.BrowserCategory} category");
            }
        }

        foreach (var function in Script.Value.Markers.Select(m => m.Scenario).Distinct())
        {
            if (!Script.Value.DefaultRun.Contains(function.Split(' ')[1]))
            {
                failures.Add($"{function} is not in the script's default run");
            }
        }

        failures.Should().BeEmpty();

        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot, ".github", "workflows", "scenarios.yml"));
        workflow.Should().Contain("--filter \"TestCategory=Scenario\"")
            .And.Contain("--filter \"TestCategory=Browser\"")
            .And.Contain("--filter \"TestCategory=Soak\"")
            .And.MatchRegex(@"(?m)^\s*run: tests/emulator/deploy-scenarios\.sh\s*$", "the container job runs every container scenario");
    }

    [TestMethod]
    public void TheDocument_HasNoInstrumentOrBenchSteps()
    {
        var path = Path.Combine(RepositoryRoot, "docs", "commissioning.md");
        File.ReadAllLines(path)
            .Select((line, index) => (Line: index + 1, Text: line))
            .Where(l => Regex.IsMatch(l.Text, @"\b(multimeter|meter|oscilloscope|scope|bench)s?\b", RegexOptions.IgnoreCase))
            .Select(l => $"line {l.Line}: {l.Text}")
            .Should().BeEmpty("every check runs against the emulator");
    }

    private static string Marker(string check, string? step)
        => step is null ? $"CommissioningCheck(\"{check}\")" : $"CommissioningCheck(\"{check}\", \"{step}\")";

    private static string Name(MethodInfo method) => $"{method.DeclaringType!.Name}.{method.Name}";

    private static bool Exists(string scenario)
        => scenario.StartsWith(ScriptName + " ", StringComparison.Ordinal)
            ? Script.Value.Functions.Contains(scenario)
            : typeof(ScenarioCoverageTests).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                .Any(m => Name(m) == scenario);

    private static IEnumerable<MethodInfo> AttributedMethods()
        => typeof(ScenarioCoverageTests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(m => m.IsDefined(typeof(CommissioningCheckAttribute)));

    private static IReadOnlyList<Coverage> ReadAttributes()
        => [.. AttributedMethods().SelectMany(m => m.GetCustomAttributes<CommissioningCheckAttribute>().Select(a => new Coverage(a.Check, a.Step, Name(m))))];

    /// <summary>
    /// The container scenarios: each <c>scenario_name() {</c> function of the script, the
    /// <c># CommissioningCheck(...)</c> lines inside it, and the scenarios the script runs without arguments.
    /// </summary>
    private static ContainerScript ReadScript()
    {
        var path = Path.Combine(RepositoryRoot, "tests", "emulator", ScriptName);
        var functions = new HashSet<string>(StringComparer.Ordinal);
        var markers = new List<Coverage>();
        var defaultRun = new HashSet<string>(StringComparer.Ordinal);
        string? function = null;
        foreach (var line in File.ReadLines(path))
        {
            var start = Regex.Match(line, @"^scenario_([a-z0-9_]+)\(\) \{$");
            var marker = Regex.Match(line, @"^\s*# CommissioningCheck\(""(C\d+)""(?:, ""([^""]+)"")?\)\s*$");
            var run = Regex.Match(line, @"^\(\( \$\{#scenarios\[@\]\} > 0 \)\) \|\| scenarios=\(([a-z0-9_ ]+)\)$");
            if (start.Success)
            {
                function = $"{ScriptName} {start.Groups[1].Value}";
                functions.Add(function);
            }
            else if (line == "}")
            {
                function = null;
            }
            else if (marker.Success)
            {
                function.Should().NotBeNull("a CommissioningCheck line belongs inside a scenario function: {0}", line);
                markers.Add(new Coverage(marker.Groups[1].Value, marker.Groups[2].Success ? marker.Groups[2].Value : null, function!));
            }
            else if (run.Success)
            {
                defaultRun.UnionWith(run.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }
        }

        defaultRun.Should().NotBeEmpty("the script runs its scenarios by default");
        return new ContainerScript(functions, markers, defaultRun);
    }

    /// <summary>
    /// The Checks section of docs/commissioning.md: each <c>### Cn. Title</c> heading, its step table and the top-level
    /// items of its installation assumptions list.
    /// </summary>
    private static IReadOnlyList<CheckSection> ReadDocument()
    {
        var lines = File.ReadAllLines(Path.Combine(RepositoryRoot, "docs", "commissioning.md"));
        var checks = lines.SkipWhile(l => l != "## Checks").Skip(1).TakeWhile(l => !l.StartsWith("## ", StringComparison.Ordinal)).ToList();
        checks.Should().NotBeEmpty("docs/commissioning.md has a Checks section");

        var sections = new List<CheckSection>();
        var index = 0;
        while (index < checks.Count)
        {
            var heading = Regex.Match(checks[index], @"^### (C\d+)\. \S");
            if (!heading.Success)
            {
                index++;
                continue;
            }

            var body = checks.Skip(index + 1).TakeWhile(l => !l.StartsWith("### ", StringComparison.Ordinal)).ToList();
            index += body.Count + 1;
            var check = heading.Groups[1].Value;

            body.Count(l => l == StepTableHeader).Should().Be(1, "{0} has one step table", check);
            var table = body.SkipWhile(l => l != StepTableHeader).TakeWhile(l => l.StartsWith('|')).ToList();
            table.Should().HaveCountGreaterThan(2, "{0}'s step table has rows", check);
            table[1].Should().Be("|---|---|---|");
            var rows = new List<StepRow>();
            foreach (var row in table.Skip(2))
            {
                var cells = row.Split('|', StringSplitOptions.TrimEntries);
                cells.Should().HaveCount(5, "{0}'s step rows have three cells: {1}", check, row);
                var scenarios = Regex.Matches(cells[3], "`([^`]+)`").Select(m => m.Groups[1].Value).ToList();
                rows.Add(new StepRow(cells[1], cells[2], cells[3], scenarios));
            }

            var assumptions = new List<string>();
            foreach (var line in body.SkipWhile(l => l != AssumptionsHeading).Skip(1))
            {
                if (line.StartsWith("- **", StringComparison.Ordinal))
                {
                    assumptions.Add(line);
                }
                else if (assumptions.Count > 0 && line.StartsWith("  ", StringComparison.Ordinal))
                {
                    assumptions[^1] += " " + line.Trim();
                }
                else if (assumptions.Count > 0 && line.Length > 0)
                {
                    break;
                }
            }

            sections.Add(new CheckSection(check, rows, assumptions));
        }

        return sections;
    }

    private sealed record Coverage(string Check, string? Step, string Scenario);

    private sealed record StepRow(string Step, string Checked, string ScenarioCell, IReadOnlyList<string> Scenarios);

    private sealed record CheckSection(string Check, IReadOnlyList<StepRow> Rows, IReadOnlyList<string> Assumptions);

    private sealed record ContainerScript(IReadOnlySet<string> Functions, IReadOnlyList<Coverage> Markers, IReadOnlySet<string> DefaultRun);
}
