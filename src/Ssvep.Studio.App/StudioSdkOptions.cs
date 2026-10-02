using System.IO;
using Ssvep.Speller.Core.Devices;

namespace Ssvep.Studio.App;

internal static class StudioSdkOptions
{
    public static SynchroniBridgeOptions CreateDefault()
    {
        var options = SynchroniBridgeOptions.CreateDefault();
        string packagedPython = Path.Combine(AppContext.BaseDirectory, "runtime", "python", "python.exe");
        if (File.Exists(packagedPython))
            return options with { PythonExecutablePath = packagedPython, PythonPrefixArguments = [] };
        // Source builds use the same dependency versions as the local release package.
        if (Path.GetFileNameWithoutExtension(options.PythonExecutablePath).Equals("uv", StringComparison.OrdinalIgnoreCase))
            return options with { PythonPrefixArguments = ["run", "--python", "3.11", "--with", "numpy==2.4.6",
                "--with", "bleak==3.0.2", "--with", "setuptools==82.0.1", "python"] };
        return options;
    }
}
