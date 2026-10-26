namespace Dinamico.LoadEngine.Tests;

internal static class CliTestAssembly
{
    internal static string PathForCurrentBuild()
    {
        const string fileName = "Dinamico.LoadEngine.Local.dll";
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        // --artifacts-path: bin/<project>/<configuration>, with the executable in a sibling project.
        if (output.Parent?.Parent is { } artifactsBin)
        {
            string sibling = Path.Combine(artifactsBin.FullName, "Dinamico.LoadEngine.Local", output.Name, fileName);
            if (File.Exists(sibling) && File.Exists(Path.ChangeExtension(sibling, ".runtimeconfig.json"))) return sibling;
        }
        // Traditional layout: <repository>/tests/<project>/bin/<configuration>/net10.0.
        var directory = output;
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Dinamico.slnx"))) directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("Repository root unavailable for the traditional build layout.");
        string executable = Path.Combine(directory.FullName, "src", "Dinamico.LoadEngine.Local", "bin", output.Parent!.Name, "net10.0", fileName);
        if (!File.Exists(executable)) throw new InvalidOperationException("Build the CLI in the same configuration as the tests.");
        return executable;
    }
}
