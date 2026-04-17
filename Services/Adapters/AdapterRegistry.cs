namespace Ripple.Services.Adapters;

/// <summary>
/// Immutable in-memory registry of loaded adapters, keyed by canonical name
/// and alias. Constructed once at startup via AdapterRegistry.LoadEmbedded.
///
/// Lookup is case-insensitive on the shell family name, matching the
/// existing ConsoleManager.NormalizeShellFamily convention (bash, pwsh, cmd,
/// zsh, powershell, etc.).
/// </summary>
public sealed class AdapterRegistry
{
    private readonly Dictionary<string, Adapter> _byName;

    /// <summary>
    /// Process-wide default registry, initialized once at startup by
    /// Program.cs. Read by ConsoleWorker / ConsoleManager to look up the
    /// adapter for a shell without plumbing the registry through
    /// constructors. Null until Program.cs calls SetDefault.
    /// </summary>
    public static AdapterRegistry? Default { get; private set; }

    public static void SetDefault(AdapterRegistry registry) => Default = registry;

    private AdapterRegistry(Dictionary<string, Adapter> byName)
    {
        _byName = byName;
    }

    /// <summary>
    /// Default external adapter directory: ~/.ripple/adapters. User-dropped
    /// YAMLs here override embedded adapters of the same name, so a user
    /// iterating on a local adapter doesn't need to rebuild ripple.
    /// </summary>
    public static string DefaultExternalDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ripple",
            "adapters");

    /// <summary>
    /// Load all adapters shipped with the ripple binary. Parse errors for
    /// individual adapters are surfaced via the returned LoadReport, not
    /// thrown.
    /// </summary>
    public static (AdapterRegistry Registry, LoadReport Report) LoadEmbedded()
        => LoadFrom(AdapterLoader.LoadEmbedded());

    /// <summary>
    /// Load embedded adapters plus any YAMLs found in the user-scoped
    /// external adapter directory. External adapters override embedded
    /// adapters of the same name (users can edit a local pwsh.yaml without
    /// forking the repo); name clashes among external adapters, or between
    /// external and embedded aliases, surface as collisions in LoadReport.
    /// </summary>
    public static (AdapterRegistry Registry, LoadReport Report) LoadDefault()
    {
        var embedded = AdapterLoader.LoadEmbedded();
        var external = AdapterLoader.LoadFromDirectory(DefaultExternalDirectory);
        return LoadFrom(embedded, external);
    }

    private static (AdapterRegistry Registry, LoadReport Report) LoadFrom(params AdapterLoader.LoadResult[] sources)
    {
        var byName = new Dictionary<string, Adapter>(StringComparer.OrdinalIgnoreCase);
        var loadedOrigins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var collisions = new List<string>();
        var overrides = new List<string>();
        var parseErrors = new List<(string Resource, string Error)>();

        foreach (var source in sources)
        {
            parseErrors.AddRange(source.Errors);

            foreach (var loaded in source.Adapters)
            {
                var adapter = loaded.Adapter;
                var originLabel = loaded.Source == AdapterLoader.AdapterSource.External
                    ? $"external:{Path.GetFileName(loaded.Origin)}"
                    : $"embedded:{loaded.Origin}";

                // External adapters override embedded ones of the same name
                // even when the embedded one is already registered under
                // aliases — the user's intent is "replace the built-in".
                if (byName.TryGetValue(adapter.Name, out var existing))
                {
                    var existingOrigin = loadedOrigins.GetValueOrDefault(adapter.Name, "?");
                    if (loaded.Source == AdapterLoader.AdapterSource.External &&
                        existingOrigin.StartsWith("embedded:"))
                    {
                        RemoveAdapter(byName, loadedOrigins, existing);
                        overrides.Add($"external adapter '{adapter.Name}' from {loaded.Origin} overrode {existingOrigin}");
                    }
                    else
                    {
                        collisions.Add($"adapter name '{adapter.Name}' from {originLabel} collides with existing registration ({existingOrigin})");
                        continue;
                    }
                }

                byName[adapter.Name] = adapter;
                loadedOrigins[adapter.Name] = originLabel;

                if (adapter.Aliases != null)
                {
                    foreach (var alias in adapter.Aliases)
                    {
                        if (byName.TryGetValue(alias, out var aliasExisting) && !ReferenceEquals(aliasExisting, adapter))
                        {
                            collisions.Add($"alias '{alias}' for adapter '{adapter.Name}' ({originLabel}) collides with existing registration ({loadedOrigins.GetValueOrDefault(alias, "?")})");
                            continue;
                        }
                        byName[alias] = adapter;
                        loadedOrigins[alias] = originLabel;
                    }
                }
            }
        }

        var registry = new AdapterRegistry(byName);
        var distinctLoaded = byName.Values.Distinct()
            .Select(a => $"{a.Name}({loadedOrigins.GetValueOrDefault(a.Name, "?").Split(':')[0]})")
            .ToList();
        var report = new LoadReport(
            Loaded: distinctLoaded,
            ParseErrors: parseErrors,
            Collisions: collisions,
            Overrides: overrides);

        return (registry, report);
    }

    private static void RemoveAdapter(
        Dictionary<string, Adapter> byName,
        Dictionary<string, string> origins,
        Adapter adapter)
    {
        var keysToRemove = byName.Where(kv => ReferenceEquals(kv.Value, adapter)).Select(kv => kv.Key).ToList();
        foreach (var key in keysToRemove)
        {
            byName.Remove(key);
            origins.Remove(key);
        }
    }

    /// <summary>
    /// Look up an adapter by shell family name or alias. Returns null if
    /// no adapter matches. Name comparison is case-insensitive.
    /// </summary>
    public Adapter? Find(string name)
        => _byName.TryGetValue(name, out var adapter) ? adapter : null;

    public IReadOnlyCollection<Adapter> All => _byName.Values.Distinct().ToList();

    public int Count => _byName.Values.Distinct().Count();

    public record LoadReport(
        IReadOnlyList<string> Loaded,
        IReadOnlyList<(string Resource, string Error)> ParseErrors,
        IReadOnlyList<string> Collisions,
        IReadOnlyList<string> Overrides)
    {
        public bool HasErrors => ParseErrors.Count > 0 || Collisions.Count > 0;

        public string Summary()
        {
            var parts = new List<string>
            {
                $"{Loaded.Count} loaded ({string.Join(", ", Loaded)})"
            };
            if (Overrides.Count > 0)
                parts.Add($"{Overrides.Count} override(s): {string.Join("; ", Overrides)}");
            if (ParseErrors.Count > 0)
                parts.Add($"{ParseErrors.Count} parse error(s): {string.Join("; ", ParseErrors.Select(e => $"{e.Resource}: {e.Error}"))}");
            if (Collisions.Count > 0)
                parts.Add($"{Collisions.Count} collision(s): {string.Join("; ", Collisions)}");
            return string.Join(" | ", parts);
        }
    }
}
