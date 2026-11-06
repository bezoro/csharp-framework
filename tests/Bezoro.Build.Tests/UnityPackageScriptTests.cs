using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Bezoro.Build.Tests;

public sealed class UnityPackageScriptTests
{
    [Fact]
    public void StagingIncludesDependenciesAndExcludesHostAssemblies()
    {
        using var fixture = new StagingFixture();
        fixture.AddProject("Bezoro.One", ("System.Collections.Immutable.dll", "immutable"),
            ("System.Memory.dll", "host memory"));
        fixture.AddProject("Bezoro.Two", ("System.Collections.Immutable.dll", "immutable"),
            ("System.Text.Json.dll", "json"));

        var result = fixture.Run();

        result.ExitCode.Should().Be(0, result.Error);
        File.ReadAllText(Path.Combine(fixture.Path, "artifacts/package/Runtime/System.Collections.Immutable.dll"))
            .Should().Be("immutable");
        File.Exists(Path.Combine(fixture.Path, "artifacts/package/Runtime/System.Text.Json.dll")).Should().BeTrue();
        File.Exists(Path.Combine(fixture.Path, "artifacts/package/Runtime/System.Memory.dll")).Should().BeFalse();
        File.Exists(Path.Combine(fixture.Path, "artifacts/package/Runtime/Bezoro.One.README.md")).Should().BeTrue();
        File.ReadAllText(Path.Combine(fixture.Path, "artifacts/package/package.json")).Should().Contain("1.2.3");
    }

    [Fact]
    public void StagingPreservesXmlAndAssemblyMetadataAcrossRebuilds()
    {
        using var fixture = new StagingFixture();
        fixture.AddProject("Bezoro.One");
        string template = Path.Combine(fixture.Path, "unity-package/Runtime");
        string xmlMetadata = File.ReadAllText(Path.Combine(template, "Bezoro.One.xml.meta"));
        string assemblyMetadata = File.ReadAllText(Path.Combine(template, "Bezoro.One.dll.meta"));

        var first = fixture.Run();

        first.ExitCode.Should().Be(0, first.Error);
        string output = Path.Combine(fixture.Path, "artifacts/package/Runtime");
        File.ReadAllText(Path.Combine(output, "Bezoro.One.xml")).Should().Be("<doc>Bezoro.One</doc>");
        File.ReadAllText(Path.Combine(output, "Bezoro.One.xml.meta")).Should().Be(xmlMetadata);
        File.ReadAllText(Path.Combine(output, "Bezoro.One.dll.meta")).Should().Be(assemblyMetadata);
        Regex.Match(xmlMetadata, @"(?m)^guid: ([0-9a-f]{32})$").Success.Should().BeTrue();
        xmlMetadata.Should().NotBe(assemblyMetadata);

        File.WriteAllText(Path.Combine(fixture.Path, "src/Bezoro.One/bin/Release/netstandard2.1/Bezoro.One.xml"),
            "<doc>Updated documentation</doc>");
        var rebuilt = fixture.Run();

        rebuilt.ExitCode.Should().Be(0, rebuilt.Error);
        File.ReadAllText(Path.Combine(output, "Bezoro.One.xml")).Should().Be("<doc>Updated documentation</doc>");
        File.ReadAllText(Path.Combine(output, "Bezoro.One.xml.meta")).Should().Be(xmlMetadata);
        File.ReadAllText(Path.Combine(output, "Bezoro.One.dll.meta")).Should().Be(assemblyMetadata);
    }

    [Fact]
    public void StagingRejectsXmlDocumentationWithoutStableMetadata()
    {
        using var fixture = new StagingFixture();
        fixture.AddProject("Bezoro.One");
        File.Delete(Path.Combine(fixture.Path, "unity-package/Runtime/Bezoro.One.xml.meta"));

        var result = fixture.Run();

        result.ExitCode.Should().NotBe(0);
        result.Error.Should().Contain("Missing Unity metadata").And.Contain("Runtime/Bezoro.One.xml");
    }

    [Theory]
    [InlineData("fileFormatVersion: 2\nguid: invalid\n")]
    [InlineData("fileFormatVersion: 2\\nguid: 11111111111111111111111111111111\\n")]
    public void StagingRejectsMalformedXmlMetadata(string metadata)
    {
        using var fixture = new StagingFixture();
        fixture.AddProject("Bezoro.One");
        File.WriteAllText(Path.Combine(fixture.Path, "unity-package/Runtime/Bezoro.One.xml.meta"), metadata);

        var result = fixture.Run();

        result.ExitCode.Should().NotBe(0);
        result.Error.Should().Contain("Invalid Unity metadata GUID").And.Contain("Runtime/Bezoro.One.xml");
    }

    [Fact]
    public void StagingRejectsDuplicateMetadataGuids()
    {
        using var fixture = new StagingFixture();
        fixture.AddProject("Bezoro.One");
        fixture.AddProject("Bezoro.Two");
        string runtime = Path.Combine(fixture.Path, "unity-package/Runtime");
        File.Copy(Path.Combine(runtime, "Bezoro.One.xml.meta"), Path.Combine(runtime, "Bezoro.Two.xml.meta"), overwrite: true);

        var result = fixture.Run();

        result.ExitCode.Should().NotBe(0);
        result.Error.Should().Contain("Duplicate Unity metadata GUID");
    }

    [Fact]
    public void StagingResolvesFlattenedReadmeLinksToSourceDocumentation()
    {
        using var fixture = new StagingFixture();
        fixture.AddProject("Bezoro.One");
        File.WriteAllText(Path.Combine(fixture.Path, "src/Bezoro.One/README.md"),
            "[Details](Details/README.md#usage)\n[Notes](<Details/My Notes.md#topic> \"Notes\")\n" +
            "[Local](#local)\n[External](https://example.com/guide)\n");

        var result = fixture.Run();

        result.ExitCode.Should().Be(0, result.Error);
        string readme = File.ReadAllText(Path.Combine(fixture.Path, "artifacts/package/Runtime/Bezoro.One.README.md"));
        readme.Should().Contain("https://github.com/bezoro/csharp-framework/blob/main/src/Bezoro.One/Details/README.md#usage");
        readme.Should().Contain("https://github.com/bezoro/csharp-framework/blob/main/src/Bezoro.One/Details/My%20Notes.md#topic \"Notes\"");
        readme.Should().Contain("[Local](#local)").And.Contain("[External](https://example.com/guide)");
    }

    [Fact]
    public void StagingTreatsOutputNamesLiterallyAndReplacesOldArtifacts()
    {
        using var fixture = new StagingFixture();
        fixture.AddProject("Bezoro.One");
        string output = Path.Combine(fixture.Path, "artifacts/package[one]");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "obsolete.dll"), "old");

        var result = fixture.Run("artifacts/package[one]");

        result.ExitCode.Should().Be(0, result.Error);
        File.Exists(Path.Combine(output, "obsolete.dll")).Should().BeFalse();
        File.Exists(Path.Combine(output, "Runtime/Bezoro.One.dll")).Should().BeTrue();
    }

    [Fact]
    public void StagingRejectsConflictingDependencyVersions()
    {
        using var fixture = new StagingFixture();
        fixture.AddProject("Bezoro.One", ("System.Text.Json.dll", "version one"));
        fixture.AddProject("Bezoro.Two", ("System.Text.Json.dll", "version two"));

        var result = fixture.Run();

        result.ExitCode.Should().NotBe(0);
        result.Error.Should().Contain("Conflicting runtime dependency");
    }

    [Fact]
    public void StagingRejectsMissingProjectAssembly()
    {
        using var fixture = new StagingFixture();
        fixture.AddProject("Bezoro.One");
        File.Delete(Path.Combine(fixture.Path, "src/Bezoro.One/bin/Release/netstandard2.1/Bezoro.One.dll"));

        var result = fixture.Run();

        result.ExitCode.Should().NotBe(0);
        result.Error.Should().Contain("Missing runtime assembly");
    }

    [Theory]
    [InlineData(".")]
    [InlineData("src")]
    [InlineData("unity-package")]
    [InlineData(".git")]
    [InlineData(".github")]
    [InlineData("AGENTS.md")]
    [InlineData("Directory.Build.props")]
    [InlineData("artifacts")]
    [InlineData("../outside")]
    public void StagingRejectsSourceAndAncestorOutputPaths(string output)
    {
        using var fixture = new StagingFixture();

        var result = fixture.Run(output);

        result.ExitCode.Should().NotBe(0);
        File.Exists(Path.Combine(fixture.Path, "unity-package/package.json")).Should().BeTrue();
        result.Error.Should().Contain("OutputPath must be a staging directory");
    }

    [Fact]
    public void StagingRejectsOutputThroughASymbolicLink()
    {
        // Unix symlinks can be created without the Windows symlink privilege.
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new StagingFixture();
        fixture.AddProject("Bezoro.One");
        Directory.CreateDirectory(Path.Combine(fixture.Path, "artifacts"));
        Directory.CreateSymbolicLink(Path.Combine(fixture.Path, "artifacts/link"),
            Path.Combine(fixture.Path, "src"));

        var result = fixture.Run("artifacts/link/package");

        result.ExitCode.Should().NotBe(0);
        result.Error.Should().Contain("symbolic links or junctions");
        Directory.Exists(Path.Combine(fixture.Path, "src/package")).Should().BeFalse();
    }

    private sealed class StagingFixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bezoro-staging", Guid.NewGuid().ToString("N"));

        public StagingFixture()
        {
            string root = RepositoryPaths.FindRoot();
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "scripts"));
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "src"));
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "unity-package"));
            foreach (var script in new[] { "Build-UnityPackage.ps1", "Get-PackageVersion.ps1" })
                File.Copy(System.IO.Path.Combine(root, "scripts", script), System.IO.Path.Combine(Path, "scripts", script));
            File.WriteAllText(System.IO.Path.Combine(Path, "unity-package/package.json"), "{\"name\":\"test\",\"version\":\"0.0.0\"}");
            AddMetadata("package.json");
            AddMetadata("Runtime");
        }

        public void AddProject(string name, params (string Name, string Content)[] dependencies)
        {
            var project = System.IO.Path.Combine(Path, "src", name);
            var output = System.IO.Path.Combine(project, "bin/Release/netstandard2.1");
            Directory.CreateDirectory(output);
            File.WriteAllText(System.IO.Path.Combine(project, name + ".csproj"), "<Project />");
            File.WriteAllText(System.IO.Path.Combine(project, "README.md"), name);
            File.WriteAllText(System.IO.Path.Combine(output, name + ".dll"), name);
            File.WriteAllText(System.IO.Path.Combine(output, name + ".xml"), "<doc>" + name + "</doc>");
            AddMetadata("Runtime/" + name + ".dll");
            AddMetadata("Runtime/" + name + ".README.md");
            AddMetadata("Runtime/" + name + ".xml");
            foreach (var dependency in dependencies)
            {
                File.WriteAllText(System.IO.Path.Combine(output, dependency.Name), dependency.Content);
                AddMetadata("Runtime/" + dependency.Name);
            }
        }

        private void AddMetadata(string assetPath)
        {
            string metadata = System.IO.Path.Combine(Path, "unity-package", assetPath + ".meta");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(metadata)!);
            File.WriteAllText(metadata, "fileFormatVersion: 2\nguid: " + Guid.NewGuid().ToString("N") + "\n");
        }

        public (int ExitCode, string Error) Run(string output = "artifacts/package")
        {
            using var process = new Process { StartInfo = new ProcessStartInfo("pwsh")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            }};
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File",
                System.IO.Path.Combine(Path, "scripts/Build-UnityPackage.ps1"), "-PackageVersion", "1.2.3", "-OutputPath", output })
                process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        }

        public void Dispose() => Directory.Delete(Path, true);
    }
}
