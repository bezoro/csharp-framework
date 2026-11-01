using System;
using System.Diagnostics;
using System.IO;
using FluentAssertions;
using Xunit;

namespace Bezoro.Build.Tests;

public sealed class UnityPackagePublishScriptTests
{
    [Fact]
    public void PublishingCreatesAnIdempotentBranchAndReplacesObsoleteArtifacts()
    {
        using var fixture = new PublishFixture();
        fixture.WritePackageFile("package.json", "{\"name\":\"test\",\"version\":\"1.2.3\"}");
        fixture.WritePackageFile("Runtime/Current.dll", "first assembly");
        fixture.WritePackageFile("Runtime/Obsolete.dll", "obsolete assembly");

        var first = fixture.Publish("1111111111111111111111111111111111111111");
        first.ExitCode.Should().Be(0, first.Output);
        string firstHead = fixture.ReadGit("rev-parse", "upm");
        fixture.ReadGit("log", "-1", "--format=%s", "upm")
            .Should().StartWith("chore(upm): ").And.Contain("1111111111111111111111111111111111111111");
        fixture.ReadGit("show", "upm:Runtime/Current.dll").Should().Be("first assembly");

        var unchanged = fixture.Publish("1111111111111111111111111111111111111111");
        unchanged.ExitCode.Should().Be(0, unchanged.Output);
        fixture.ReadGit("rev-parse", "upm").Should().Be(firstHead);

        fixture.WritePackageFile("Runtime/Current.dll", "replacement assembly");
        fixture.RemovePackageFile("Runtime/Obsolete.dll");
        var replacement = fixture.Publish("2222222222222222222222222222222222222222");
        replacement.ExitCode.Should().Be(0, replacement.Output);
        fixture.ReadGit("show", "upm:Runtime/Current.dll").Should().Be("replacement assembly");
        fixture.ReadGit("ls-tree", "-r", "--name-only", "upm").Should().NotContain("Obsolete.dll");
        fixture.ReadGit("rev-list", "--count", "upm").Should().Be("2");
        fixture.ReadGit("rev-parse", "upm^").Should().Be(firstHead);
    }

    private sealed class PublishFixture : IDisposable
    {
        private const string Remote = "https://github.com/fixture/repository.git";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "bezoro-publish", Guid.NewGuid().ToString("N"));
        private readonly string _bareRepository;
        private readonly string _package;
        private readonly string _gitConfig;
        private readonly string _script;

        public PublishFixture()
        {
            string repository = RepositoryPaths.FindRoot();

            Directory.CreateDirectory(_root);
            _bareRepository = Path.Combine(_root, "repository.git");
            _package = Path.Combine(_root, "package");
            _gitConfig = Path.Combine(_root, "gitconfig");
            _script = Path.Combine(repository, "scripts", "Publish-UnityPackageBranch.ps1");
            Directory.CreateDirectory(_package);
            File.WriteAllText(_gitConfig, string.Empty);
            Git("init", "--bare", _bareRepository);
            // Rewrite only this fixture URL. No request reaches GitHub or uses real credentials.
            Git("config", "--file", _gitConfig,
                "url." + new Uri(_bareRepository).AbsoluteUri + ".insteadOf", Remote);
        }

        public void WritePackageFile(string relativePath, string content)
        {
            string path = Path.Combine(_package, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void RemovePackageFile(string relativePath) => File.Delete(Path.Combine(_package, relativePath));

        public (int ExitCode, string Output) Publish(string sourceRevision) => Run("pwsh", sourceRevision,
            "-NoProfile", "-NonInteractive", "-File", _script, "-PackagePath", _package, "-Branch", "upm");

        public string ReadGit(params string[] arguments)
        {
            var allArguments = new string[arguments.Length + 2];
            allArguments[0] = "--git-dir";
            allArguments[1] = _bareRepository;
            arguments.CopyTo(allArguments, 2);
            return Git(allArguments).Output.Trim();
        }

        private (int ExitCode, string Output) Git(params string[] arguments)
        {
            var result = Run("git", null, arguments);
            result.ExitCode.Should().Be(0, result.Output);
            return result;
        }

        private (int ExitCode, string Output) Run(string executable, string? revision, params string[] arguments)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    WorkingDirectory = _root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            process.StartInfo.Environment["GIT_CONFIG_GLOBAL"] = _gitConfig;
            process.StartInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            process.StartInfo.Environment["GITHUB_TOKEN"] = "fixture-token";
            process.StartInfo.Environment["GITHUB_REPOSITORY"] = "fixture/repository";
            if (revision is not null) process.StartInfo.Environment["GITHUB_SHA"] = revision;
            foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new TimeoutException("Package publishing fixture did not finish within 30 seconds.");
            }
            return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        }

        public void Dispose()
        {
            if (!Directory.Exists(_root)) return;
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
    }
}
