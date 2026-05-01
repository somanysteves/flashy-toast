namespace FlashyToast;

internal static class Installer
{
    private const string ShortcutFileName = "flashy-toast.lnk";

    public static int Install()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            Console.Error.WriteLine("Install failed: could not resolve current exe path.");
            return 1;
        }

        var shortcutPath = ShortcutPath();
        var workingDir = Path.GetDirectoryName(exePath) ?? "";

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("WScript.Shell ProgID not found");
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(shortcutPath);
            link.TargetPath = exePath;
            link.WorkingDirectory = workingDir;
            link.WindowStyle = 7; // minimized
            link.Description = "flashy-toast: flash taskbar on Action Center toasts";
            link.Save();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Install failed: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }

        Console.WriteLine($"Installed: {shortcutPath}");
        Console.WriteLine($"Target:    {exePath}");
        Console.WriteLine($"Log file:  {Program.LogFilePath()}");
        Console.WriteLine("Will run at next login. Run with --uninstall to remove.");
        return 0;
    }

    public static int Uninstall()
    {
        var shortcutPath = ShortcutPath();
        if (!File.Exists(shortcutPath))
        {
            Console.WriteLine($"Not installed (no shortcut at {shortcutPath}).");
            return 0;
        }

        try
        {
            File.Delete(shortcutPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Uninstall failed: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }

        Console.WriteLine($"Removed: {shortcutPath}");
        return 0;
    }

    private static string ShortcutPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), ShortcutFileName);
}
