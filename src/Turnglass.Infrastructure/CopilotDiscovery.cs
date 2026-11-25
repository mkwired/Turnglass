using System.Text.RegularExpressions;
using Turnglass.Core;

namespace Turnglass.Infrastructure;

public static partial class CopilotDiscovery
{
    private static readonly EnumerationOptions _entries = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public static IReadOnlyList<CopilotSourceRoot> GetDefaults()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var result = new List<CopilotSourceRoot>
        {
            new("shared", Path.Combine(profile, ".copilot", "session-state")),
        };
        if (OperatingSystem.IsWindows())
        {
            result.Add(new("visual-studio", Path.Combine(local, "Microsoft", "VisualStudio", "CopilotCli", "session-state")));
        }

        var codeBase = OperatingSystem.IsMacOS() ? Path.Combine(profile, "Library", "Application Support") : roaming;
        foreach (var edition in new[] { "Code", "Code - Insiders" })
        {
            result.Add(new("vscode", Path.Combine(codeBase, edition, "User")));
        }

        return result;
    }

    internal static IReadOnlyList<CopilotSourceFile> EnumerateFiles(CopilotSourceRoot root)
    {
        var path = Path.GetFullPath(root.Path);
        if (!root.Enabled)
        {
            return [];
        }

        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException("The configured Copilot source directory does not exist.");
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Copilot source directories must not be symbolic links or reparse points.");
        }

        var files = new List<CopilotSourceFile>();
        switch (root.Kind)
        {
            case "shared":
            case "visual-studio":
                FindSdkFiles(path, root.Kind, files);
                break;
            case "vscode":
                FindDebugRoots(path, files, true);
                break;
            default:
                throw new ArgumentException("Unsupported Copilot source kind.", nameof(root));
        }

        return [.. files.DistinctBy(file => file.Path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).OrderBy(file => file.Path, StringComparer.Ordinal)];
    }

    private static void FindSdkFiles(string path, string client, List<CopilotSourceFile> files)
    {
        var sessionRoot = Path.Combine(path, "session-state");
        if (IsDirectory(sessionRoot))
        {
            path = sessionRoot;
        }

        AddSdkSession(path, client, files);
        foreach (var directory in Directories(path))
        {
            AddSdkSession(directory, client, files);
        }
    }

    private static void AddSdkSession(string path, string client, List<CopilotSourceFile> files)
    {
        foreach (var file in Directory.EnumerateFiles(path, "events*.jsonl", _entries))
        {
            if (SdkEventFile().IsMatch(Path.GetFileName(file)))
            {
                files.Add(new(file, client, CopilotSourceKind.Sdk, Path.GetFileName(path)));
            }
        }
    }

    private static void FindDebugRoots(string path, List<CopilotSourceFile> files, bool includeProfiles)
    {
        var name = Path.GetFileName(path);
        if (name.Equals("debug-logs", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var session in Directories(path))
            {
                AddDebugSession(session, files);
            }

            return;
        }

        if (File.Exists(Path.Combine(path, "main.jsonl")))
        {
            AddDebugSession(path, files);
            return;
        }

        if (name.Equals("workspaceStorage", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var workspace in Directories(path))
            {
                FindExtensionDebugRoots(workspace, files);
            }

            return;
        }

        if (name.Equals("globalStorage", StringComparison.OrdinalIgnoreCase))
        {
            FindExtensionDebugRoots(path, files);
            return;
        }

        if (name.Equals("github.copilot-chat", StringComparison.OrdinalIgnoreCase))
        {
            var debugRoot = Path.Combine(path, "debug-logs");
            if (IsDirectory(debugRoot))
            {
                FindDebugRoots(debugRoot, files, false);
            }

            return;
        }

        foreach (var storageName in new[] { "workspaceStorage", "globalStorage" })
        {
            var storage = Path.Combine(path, storageName);
            if (IsDirectory(storage))
            {
                FindDebugRoots(storage, files, false);
            }
        }

        var profiles = Path.Combine(path, "profiles");
        if (includeProfiles && IsDirectory(profiles))
        {
            foreach (var profile in Directories(profiles))
            {
                FindDebugRoots(profile, files, false);
            }
        }
    }

    private static void FindExtensionDebugRoots(string path, List<CopilotSourceFile> files)
    {
        foreach (var extension in Directories(path))
        {
            if (Path.GetFileName(extension).Equals("github.copilot-chat", StringComparison.OrdinalIgnoreCase))
            {
                FindDebugRoots(extension, files, false);
            }
        }
    }

    private static void AddDebugSession(string path, List<CopilotSourceFile> files)
    {
        var parent = Path.GetFileName(path);
        foreach (var file in Directory.EnumerateFiles(path, "*.jsonl", _entries))
        {
            // The record's sid identifies a child. Its file name is a display label and
            // is not a stable session identifier across logger versions.
            files.Add(new(file, "vscode", CopilotSourceKind.VsCodeDebug, parent));
        }
    }

    private static IEnumerable<string> Directories(string path) =>
        Directory.EnumerateDirectories(path, "*", _entries);

    private static bool IsDirectory(string path) =>
        Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    [GeneratedRegex(@"^events(?:\.\d+)?\.jsonl$", RegexOptions.CultureInvariant)]
    private static partial Regex SdkEventFile();

}
