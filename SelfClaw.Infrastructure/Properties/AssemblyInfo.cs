using System.Runtime.CompilerServices;
using System.Text;

[assembly: InternalsVisibleTo("SelfClaw.Tests")]

namespace SelfClaw.Infrastructure;

internal static class AssemblyInitializer
{
    // Decoding a cmd.exe pipe needs the system OEM code page (e.g. 936), which .NET only knows about
    // after the code-pages provider is registered; the WorkspaceShellRunner resolves it lazily per run.
    [ModuleInitializer]
    internal static void RegisterEncodings() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
}
