using System;
using System.IO;
using Microsoft.Win32;

namespace Clipboard.Windows.Native;

internal static class StartupRegistration
{
    internal static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the app’s executable path.");
        string command = $"\"{executable}\" --background";
        if (key.GetValue("Clipboard.Windows") is string existing && existing != command)
            throw new InvalidOperationException("Login is registered to a different copy. Change it from that copy before enabling this one.");
        if (enabled)
        {
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Publish Clipboard before enabling launch at login.");
            key.SetValue("Clipboard.Windows", command, RegistryValueKind.String);
        }
        else key.DeleteValue("Clipboard.Windows", throwOnMissingValue: false);
    }
}
