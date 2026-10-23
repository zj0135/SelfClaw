using System.ComponentModel;
using System.Diagnostics;

namespace SelfClaw.Tests.TestDoubles;

/// <summary>
/// Link handling for tests that exercise reparse points. A symbolic link needs Developer Mode or
/// SeCreateSymbolicLinkPrivilege, so a junction is used as the fallback; both fail on a file system without
/// reparse point support, which is the only case where a test may skip the scenario.
/// </summary>
internal static class DirectoryLinkTestHelper
{
    internal static bool TryCreate(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return TryCreateJunction(linkPath, targetPath);
        }
    }

    /// <summary>
    /// <see cref="Directory.Delete(string, bool)"/> trips over links in the tree, so they are removed first —
    /// the same way <c>ExtensionCatalog</c> deletes an uninstalled package.
    /// </summary>
    internal static void DeleteTree(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return;
        }

        var pending = new Stack<string>();
        pending.Push(rootPath);
        while (pending.Count > 0)
        {
            foreach (var child in new DirectoryInfo(pending.Pop()).GetFileSystemInfos())
            {
                if (!child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    if (child is DirectoryInfo childDirectory)
                    {
                        pending.Push(childDirectory.FullName);
                    }

                    continue;
                }

                // Deleting the entry removes the link itself; recursing would follow it or be refused.
                if (child is DirectoryInfo)
                {
                    Directory.Delete(child.FullName, false);
                }
                else
                {
                    File.Delete(child.FullName);
                }
            }
        }

        Directory.Delete(rootPath, true);
    }

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("mklink");
            startInfo.ArgumentList.Add("/J");
            startInfo.ArgumentList.Add(linkPath);
            startInfo.ArgumentList.Add(targetPath);
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return Directory.Exists(linkPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return false;
        }
    }
}
