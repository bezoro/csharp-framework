using System;
using System.Diagnostics;
using System.IO;
using FluentAssertions;
using Xunit;

namespace Bezoro.Build.Tests;

public sealed class PackageVersionScriptTests
{
	[Fact]
	public void GetPackageVersion_WhenHeadHasSemverTag_ShouldReturnTagVersion()
	{
		using var repository = TemporaryGitRepository.Create();
		repository.Commit("initial");
		repository.Tag("v2.3.4");

		var version = RunVersionScript(repository.Path);

		version.Should().Be("2.3.4");
	}

	[Fact]
	public void GetPackageVersion_WhenCommitFollowsStableSemverTag_ShouldReturnNextPatchPreviewVersion()
	{
		using var repository = TemporaryGitRepository.Create();
		repository.Commit("initial");
		repository.Tag("v1.2.3");
		repository.Commit("after-tag");

		var version = RunVersionScript(repository.Path);

		version.Should().Be("1.2.4-preview.1");
	}

	[Fact]
	public void GetPackageVersion_WhenNoSemverTagsExist_ShouldReturnInitialPreviewVersion()
	{
		using var repository = TemporaryGitRepository.Create();
		repository.Commit("first");
		repository.Commit("second");

		var version = RunVersionScript(repository.Path);

		version.Should().Be("0.1.0-preview.2");
	}

	[Theory]
	[InlineData("1.2.3-preview.9", "1.2.3-preview.10", "1.2.3-preview.10")]
	[InlineData("1.2.3-2", "1.2.3-alpha", "1.2.3-alpha")]
	[InlineData("1.2.3-alpha", "1.2.3-alpha.1", "1.2.3-alpha.1")]
	[InlineData("1.2.3-rc.1", "1.2.3", "1.2.3")]
	[InlineData("1.2.3-Beta", "1.2.3-alpha", "1.2.3-alpha")]
	[InlineData("1.2.3", "100000000000000000000.0.0", "100000000000000000000.0.0")]
	public void ExactTagsFollowSemVerPrecedence(string first, string second, string expected)
	{
		using var repository = TemporaryGitRepository.Create();
		repository.Commit("initial");
		repository.Tag(first);
		repository.Tag(second);
		RunVersionScript(repository.Path).Should().Be(expected);
	}

	[Fact]
	public void VersionOverridePreservesBuildMetadata()
	{
		var root = FindRepositoryRoot();
		var result = RunProcess(root, "pwsh", "-NoProfile", "-NonInteractive", "-File",
			System.IO.Path.Combine(root, "scripts/Get-PackageVersion.ps1"), "-VersionOverride", "v1.2.3-rc.2+build.007");
		result.ExitCode.Should().Be(0, result.Error);
		result.Output.Trim().Should().Be("1.2.3-rc.2+build.007");
	}

	[Theory]
	[InlineData("1.2.3-01")]
	[InlineData("1.2.3-alpha.01")]
	[InlineData("01.2.3")]
	public void InvalidSemVerOverridesAreRejected(string version)
	{
		var root = FindRepositoryRoot();
		var result = RunProcess(root, "pwsh", "-NoProfile", "-NonInteractive", "-File",
			System.IO.Path.Combine(root, "scripts/Get-PackageVersion.ps1"), "-VersionOverride", version);
		result.ExitCode.Should().NotBe(0);
	}

	[Theory]
	[InlineData("v1.2.3", "fix(core): reject overflow", "1.2.4-preview.1")]
	[InlineData("v1.2.3", "feat(chess): add game review", "1.3.0-preview.1")]
	[InlineData("v1.2.3", "refactor(chess)!: separate gameplay", "2.0.0-preview.1")]
	[InlineData("v0.1.3", "fix(core)!: change ownership", "0.2.0-preview.1")]
	[InlineData("v1.2.3", "fix(core): change ownership\n\nBREAKING CHANGE: foreign returns are rejected", "2.0.0-preview.1")]
	public void PreviewVersionsHonorConventionalCommitIntent(string tag, string message, string expected)
	{
		using var repository = TemporaryGitRepository.Create();
		repository.Commit("initial");
		repository.Tag(tag);
		repository.Commit(message);
		RunVersionScript(repository.Path).Should().Be(expected);
	}

	[Fact]
	public void BreakingChangesTakePrecedenceOverFeaturesAndFixes()
	{
		using var repository = TemporaryGitRepository.Create();
		repository.Commit("initial");
		repository.Tag("v1.2.3");
		repository.Commit("refactor!: remove old API");
		repository.Commit("feat: add review");
		repository.Commit("fix: reject overflow");
		RunVersionScript(repository.Path).Should().Be("2.0.0-preview.3");
	}

	private static string RunVersionScript(string repositoryPath)
	{
		var root = FindRepositoryRoot();
		var scriptPath = System.IO.Path.Combine(root, "scripts", "Get-PackageVersion.ps1");
		var result = RunProcess(root, "pwsh", "-NoProfile", "-NonInteractive", "-File", scriptPath, "-RepositoryPath", repositoryPath);

		result.ExitCode.Should().Be(0, result.Error);
		return result.Output.Trim();
	}

	private static string FindRepositoryRoot()
	{
		return RepositoryPaths.FindRoot();
	}

	private static ProcessResult RunProcess(string workingDirectory, string fileName, params string[] arguments)
	{
		var startInfo = new ProcessStartInfo(fileName)
		{
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			UseShellExecute = false,
			WorkingDirectory = workingDirectory
		};

		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
		var output = process.StandardOutput.ReadToEnd();
		var error = process.StandardError.ReadToEnd();
		process.WaitForExit();

		return new(process.ExitCode, output, error);
	}

	private readonly record struct ProcessResult(int ExitCode, string Output, string Error);

	private sealed class TemporaryGitRepository : IDisposable
	{
		private int _commitNumber;

		private TemporaryGitRepository(string path)
		{
			Path = path;
		}

		public string Path { get; }

		public static TemporaryGitRepository Create()
		{
			var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bezoro-version-tests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(path);

			var repository = new TemporaryGitRepository(path);
			repository.Git("init", "--initial-branch", "main");
			repository.Git("config", "user.name", "Bezoro Build Tests");
			repository.Git("config", "user.email", "build-tests@bezoro.local");

			return repository;
		}

		public void Commit(string message)
		{
			_commitNumber++;
			File.WriteAllText(System.IO.Path.Combine(Path, "file.txt"), _commitNumber.ToString());
			Git("add", ".");
			Git("commit", "-m", message);
		}

		public void Tag(string name) =>
			Git("tag", name);

		public void Dispose()
		{
			if (Directory.Exists(Path))
			{
				foreach (var filePath in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
				{
					File.SetAttributes(filePath, FileAttributes.Normal);
				}

				Directory.Delete(Path, true);
			}
		}

		private void Git(params string[] arguments)
		{
			var result = RunProcess(Path, "git", arguments);
			result.ExitCode.Should().Be(0, result.Error);
		}
	}
}
