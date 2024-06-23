using System.Diagnostics;

namespace Bannerlord.ReferenceAssemblies;

internal static class ProcessHelpers
{
    public static int Run(string fileName, string args, string? workingDirectory = null)
    {
        using var proc = Process.Start(new ProcessStartInfo(fileName, args)
        {
            WorkingDirectory = workingDirectory ?? ""
        });
        proc!.WaitForExit();
        return proc.ExitCode;
    }

    public static int Run(string fileName, string args, out string stdOut, string? workingDirectory = null)
    {
        using var proc = Process.Start(new ProcessStartInfo(fileName, args)
        {
            WorkingDirectory = workingDirectory ?? "",
            RedirectStandardOutput = true
        });
        proc!.WaitForExit();
        stdOut = proc.StandardOutput.ReadToEnd();
        return proc.ExitCode;
    }
}