using System.Reflection;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Ripple.Services.Adapters;

/// <summary>
/// Loads ripple adapter YAML files (schema v1) into Adapter instances.
/// Adapter YAMLs ship as embedded resources under Ripple.adapters.*.yaml.
///
/// Uses StaticDeserializerBuilder + AdapterStaticContext so deserialization
/// is AOT-safe. Switching back to the reflection-based DeserializerBuilder
/// would re-introduce IL3050 and break `dotnet publish --publish-aot`.
/// </summary>
public static class AdapterLoader
{
    private const int SupportedSchemaVersion = 1;

    private static readonly IDeserializer _deserializer =
        new StaticDeserializerBuilder(new AdapterStaticContext())
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

    /// <summary>
    /// Parse a YAML string into an Adapter. Throws InvalidOperationException
    /// on schema version mismatch or missing required fields.
    /// </summary>
    public static Adapter Parse(string yaml, string sourceName)
    {
        Adapter adapter;
        try
        {
            adapter = _deserializer.Deserialize<Adapter>(yaml)
                ?? throw new InvalidOperationException($"Adapter YAML '{sourceName}' deserialized to null");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Failed to parse adapter YAML '{sourceName}': {ex.Message}", ex);
        }

        if (adapter.Schema != SupportedSchemaVersion)
            throw new InvalidOperationException(
                $"Adapter '{sourceName}' declares schema v{adapter.Schema}, but this ripple build supports v{SupportedSchemaVersion}.");

        if (string.IsNullOrEmpty(adapter.Name))
            throw new InvalidOperationException($"Adapter '{sourceName}' has empty 'name' field");

        if (string.IsNullOrEmpty(adapter.Family))
            throw new InvalidOperationException($"Adapter '{sourceName}' (name={adapter.Name}) has empty 'family' field");

        return adapter;
    }

    /// <summary>
    /// Load all adapter YAMLs embedded in this assembly. Returns the list
    /// of successfully parsed adapters and a list of (resourceName, error)
    /// pairs for any that failed.
    ///
    /// After parsing, resolves adapter.IntegrationScript: if the YAML did
    /// not provide an inline `integration_script:` block, falls back to
    /// loading the embedded resource named by `init.script_resource`
    /// (e.g. "integration.ps1" -> Ripple.ShellIntegration.integration.ps1).
    /// This keeps ShellIntegration/*.{ps1,bash,zsh} as the single source
    /// of truth for shell integration scripts and lets adapters reference
    /// them by name without duplicating their content.
    /// </summary>
    public static LoadResult LoadEmbedded()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var adapters = new List<LoadedAdapter>();
        var errors = new List<ParseError>();

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith("Ripple.adapters.", StringComparison.Ordinal))
                continue;
            if (!resourceName.EndsWith(".yaml", StringComparison.Ordinal))
                continue;

            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName)
                    ?? throw new InvalidOperationException("resource stream was null");
                using var reader = new StreamReader(stream);
                var yaml = reader.ReadToEnd();
                var adapter = Parse(yaml, resourceName);
                ResolveIntegrationScript(adapter, assembly, externalDir: null);
                adapters.Add(new LoadedAdapter(adapter, AdapterSource.Embedded, resourceName));
            }
            catch (Exception ex)
            {
                errors.Add(new ParseError(AdapterSource.Embedded, resourceName, ex.Message));
            }
        }

        return new LoadResult(adapters, errors);
    }

    /// <summary>
    /// Load all adapter YAMLs directly from a filesystem directory. Used for
    /// user-contributed adapters under ~/.ripple/adapters/ — the user drops
    /// a YAML there, ripple picks it up on the next startup, no rebuild.
    ///
    /// Missing directory is a no-op (empty result, no error). Individual
    /// YAML parse failures are collected per file rather than aborting the
    /// whole directory, so one broken adapter can't hide a working one.
    ///
    /// script_resource is resolved relative to the YAML's own directory
    /// first (allowing fully self-contained external adapters), falling
    /// back to the embedded ShellIntegration resources (so an external
    /// adapter can reference a built-in integration script by name).
    /// </summary>
    public static LoadResult LoadFromDirectory(string directory)
    {
        var adapters = new List<LoadedAdapter>();
        var errors = new List<ParseError>();

        if (!Directory.Exists(directory))
            return new LoadResult(adapters, errors);

        var assembly = Assembly.GetExecutingAssembly();

        foreach (var filePath in Directory.EnumerateFiles(directory, "*.yaml"))
        {
            try
            {
                var yaml = File.ReadAllText(filePath);
                var adapter = Parse(yaml, filePath);
                ResolveIntegrationScript(adapter, assembly, externalDir: directory);
                adapters.Add(new LoadedAdapter(adapter, AdapterSource.External, filePath));
            }
            catch (Exception ex)
            {
                errors.Add(new ParseError(AdapterSource.External, filePath, ex.Message));
            }
        }

        return new LoadResult(adapters, errors);
    }

    /// <summary>
    /// Populate adapter.IntegrationScript. Precedence:
    ///   1. Inline integration_script: block in the YAML (already set
    ///      during Parse) → no-op here.
    ///   2. script_resource: resolved relative to externalDir (for
    ///      external adapters, enables a self-contained YAML + script
    ///      pair in ~/.ripple/adapters/).
    ///   3. script_resource: resolved as an embedded resource under
    ///      Ripple.ShellIntegration.* (for external adapters overriding
    ///      a built-in shell while still using its integration script).
    /// Throws InvalidOperationException if script_resource is set but
    /// neither the external file nor the embedded resource exists.
    /// </summary>
    private static void ResolveIntegrationScript(Adapter adapter, Assembly assembly, string? externalDir)
    {
        if (!string.IsNullOrEmpty(adapter.IntegrationScript))
            return;

        var resourceRef = adapter.Init.ScriptResource;
        if (string.IsNullOrEmpty(resourceRef))
            return;

        if (externalDir != null)
        {
            var externalPath = Path.Combine(externalDir, resourceRef);
            if (File.Exists(externalPath))
            {
                adapter.IntegrationScript = File.ReadAllText(externalPath);
                return;
            }
        }

        var fullResourceName = $"Ripple.ShellIntegration.{resourceRef}";
        using var stream = assembly.GetManifestResourceStream(fullResourceName);
        if (stream == null)
        {
            var externalHint = externalDir != null
                ? $" (also not found at '{Path.Combine(externalDir, resourceRef)}')"
                : "";
            throw new InvalidOperationException(
                $"Adapter '{adapter.Name}' references script_resource '{resourceRef}' " +
                $"but embedded resource '{fullResourceName}' was not found{externalHint}.");
        }
        using var reader = new StreamReader(stream);
        adapter.IntegrationScript = reader.ReadToEnd();
    }

    public enum AdapterSource { Embedded, External }

    public record LoadedAdapter(Adapter Adapter, AdapterSource Source, string Origin);

    /// <summary>
    /// A single parse failure from a load attempt. <see cref="Source"/>
    /// is carried through so downstream callers (LoadReport consumers,
    /// silent-mode stderr gates) can split user-actionable failures
    /// (external YAML the user dropped) from internal ones (embedded
    /// YAML compiled into the binary — a ripple bug the user can't fix).
    /// </summary>
    public record ParseError(AdapterSource Source, string Resource, string Error);

    public record LoadResult(
        IReadOnlyList<LoadedAdapter> Adapters,
        IReadOnlyList<ParseError> Errors);
}
