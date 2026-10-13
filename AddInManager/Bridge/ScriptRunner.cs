using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
#if R25 || R26 || R27
using AssemblyLoadContext = RevitAddinManager.Model.AssemblyLoadContext;
#endif

namespace RevitAddinManager.Bridge;

/// <summary>
/// Runs C# snippets that the MCP server compiled with Roslyn against this Revit's own assemblies.
/// Only the compiled bytes cross the pipe; nothing is compiled inside Revit.
/// </summary>
public static class ScriptRunner
{
    public const string EntryType = "AiScript";
    public const string EntryMethod = "Run";

    /// <summary>
    /// Assemblies the script may reference: the runtime's framework plus what Revit itself loaded from its install folder.
    /// </summary>
    public static object GetCompileReferences(string revitVersion)
    {
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        var revitDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty) ?? string.Empty;
#if R25 || R26 || R27
        // ...\dotnet\shared\Microsoft.NETCore.App\8.0.x\ -> ...\dotnet\shared\ also covers Microsoft.WindowsDesktop.App (WPF).
        var frameworkRoot = Directory.GetParent(Directory.GetParent(runtimeDir.TrimEnd('\\'))!.FullName)!.FullName;
        const bool isNetFramework = false;
#else
        var frameworkRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET");
        const bool isNetFramework = true;
#endif
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic)
            .Select(a => SafeLocation(a))
            .Where(p => !string.IsNullOrEmpty(p))
            .Where(p => p.StartsWith(revitDir, StringComparison.OrdinalIgnoreCase) ||
                        p.StartsWith(frameworkRoot, StringComparison.OrdinalIgnoreCase));

        var references = Directory.GetFiles(runtimeDir, "*.dll")
            .Concat(loaded)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new
        {
            revitVersion,
            isNetFramework,
            framework = RuntimeInformation.FrameworkDescription,
            references,
        };
    }

    public static Func<RunOutcome> Prepare(UIApplication app, byte[] assemblyBytes, byte[] pdbBytes, bool useTransaction,
        out Action cleanup)
    {
#if R25 || R26 || R27
        var alc = new AssemblyLoadContext();
        var assembly = alc.LoadFromStream(new MemoryStream(assemblyBytes), pdbBytes == null ? null : new MemoryStream(pdbBytes));
        cleanup = () => BridgeCommands.UnloadLater(alc);
#else
        var assembly = pdbBytes == null ? Assembly.Load(assemblyBytes) : Assembly.Load(assemblyBytes, pdbBytes);
        cleanup = () => { };
#endif
        var method = assembly.GetType(EntryType)?.GetMethod(EntryMethod, BindingFlags.Public | BindingFlags.Static)
                     ?? throw new InvalidOperationException($"Compiled script has no {EntryType}.{EntryMethod}.");

        return () =>
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            Transaction transaction = null;
            if (useTransaction && doc != null && !doc.IsReadOnly && !doc.IsModifiable)
            {
                transaction = new Transaction(doc, "AI script");
                transaction.Start();
            }
            try
            {
                var value = method.Invoke(null, new object[] { app, uidoc, doc });
                if (transaction != null && transaction.GetStatus() == TransactionStatus.Started)
                {
                    var status = transaction.Commit();
                    if (status != TransactionStatus.Committed)
                        return new RunOutcome { Result = "Failed", Message = $"Revit did not commit the transaction ({status}). See failures." };
                }
                return new RunOutcome { Result = "Succeeded", ReturnValue = ResultFormatter.ToJson(value) };
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                return new RunOutcome { Result = "Exception", Exception = ex.InnerException.ToString() };
            }
            finally
            {
                if (transaction != null)
                {
                    if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
                    transaction.Dispose();
                }
            }
        };
    }

    private static string SafeLocation(Assembly assembly)
    {
        try
        {
            return assembly.Location;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
