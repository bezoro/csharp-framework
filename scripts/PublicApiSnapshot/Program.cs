using System.Globalization;
using System.Runtime.Loader;
using System.Xml.Linq;
using Mono.Cecil;
using PublicApiGenerator;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
string repository = Path.GetFullPath(args[0]);
bool check = args.Contains("--check", StringComparer.Ordinal);
string baselineRoot = Path.Combine(repository, "api");
var baselines = new HashSet<string>(StringComparer.Ordinal);
var options = new ApiGeneratorOptions { IncludeAssemblyAttributes = false, TreatRecordsAsClasses = false };
bool changed = false;

foreach (string project in Directory.EnumerateFiles(Path.Combine(repository, "src"), "*.csproj", SearchOption.AllDirectories)
             .Order(StringComparer.Ordinal))
{
    string name = Path.GetFileNameWithoutExtension(project);
    string module = Path.GetDirectoryName(project)!;
    var properties = XDocument.Load(project).Descendants().First(element =>
        element.Name.LocalName is "TargetFramework" or "TargetFrameworks");
    var targets = properties.Value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    var snapshots = targets.Select(target => (Target: target, Api: ReadApi(module, name, target, options))).ToArray();
    string snapshot = string.Join("\n\n", snapshots.GroupBy(item => item.Api, StringComparer.Ordinal)
        .Select(group => "// " + string.Join(", ", group.Select(item => item.Target)) + "\n" + group.Key)) + "\n";
    string path = Path.Combine(baselineRoot, $"PublicApi.{name}.txt");
    baselines.Add(path);
    if (!check)
    {
        File.WriteAllText(path, snapshot);
    }
    else if (!File.Exists(path) || File.ReadAllText(path).ReplaceLineEndings("\n") != snapshot)
    {
        Console.Error.WriteLine($"Public API baseline changed: {Path.GetFileName(path)}");
        changed = true;
    }
}

foreach (string path in Directory.EnumerateFiles(baselineRoot, "PublicApi.*.txt"))
{
    if (baselines.Contains(path)) continue;
    if (!check)
    {
        File.Delete(path);
    }
    else
    {
        Console.Error.WriteLine($"Obsolete public API baseline: {Path.GetFileName(path)}");
        changed = true;
    }
}

if (changed)
    Console.Error.WriteLine("Review the API change and regenerate the baseline with scripts/Export-PublicApi.ps1.");
return changed ? 1 : 0;

static string ReadApi(string module, string name, string target, ApiGeneratorOptions options)
{
    string path = Path.Combine(module, "bin", "Release", target, name + ".dll");
    if (!File.Exists(path))
        throw new FileNotFoundException($"Build the Release solution before checking the public API for {name} ({target}).", path);

    var context = new AssemblyLoadContext(name + "/" + target, isCollectible: true);
    try
    {
        string api = context.LoadFromAssemblyPath(path).GeneratePublicApi(options).ReplaceLineEndings("\n").TrimEnd();
        // The declaration generator deliberately hides compiler-generated methods such as Deconstruct.
        using var metadata = AssemblyDefinition.ReadAssembly(path);
        var generated = metadata.MainModule.GetTypes()
            .Where(IsPubliclyVisible)
            .SelectMany(type => type.Methods)
            .Where(method => (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly)
                && method.SemanticsAttributes == MethodSemanticsAttributes.None
                && method.CustomAttributes.Any(attribute =>
                    attribute.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute"))
            .Select(method => "// " + method.FullName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return generated.Length == 0 ? api : api + "\n\n// Compiler-generated callable members\n" + string.Join('\n', generated);
    }
    finally
    {
        context.Unload();
    }
}

static bool IsPubliclyVisible(TypeDefinition type) => type.DeclaringType is { } parent
    ? (type.IsNestedPublic || type.IsNestedFamily || type.IsNestedFamilyOrAssembly) && IsPubliclyVisible(parent)
    : type.IsPublic;
