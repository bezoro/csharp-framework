using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace Bezoro.Build.Tests;

internal static class RepositoryPaths
{
    internal static string FindRoot([CallerFilePath] string sourceFile = "")
    {
        foreach (string? candidate in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(sourceFile), Environment.CurrentDirectory })
        {
            if (string.IsNullOrEmpty(candidate) || !Directory.Exists(candidate)) continue;
            var directory = new DirectoryInfo(candidate);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "bezoro.framework.sln"))) return directory.FullName;
                directory = directory.Parent;
            }
        }
        throw new InvalidOperationException("The build-script fixtures require a framework source checkout.");
    }
}
