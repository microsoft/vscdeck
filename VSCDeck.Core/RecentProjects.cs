namespace VSCDeck.Core;

public sealed record ProjectConfiguration(
    string ProjectIdentity, string Name, string WorkspaceUri, string Executable, bool IsWorkspace, string Environment)
{
    public string? ButtonOrderIdentity { get; init; }
    public string Id => Executable.ToLowerInvariant() + "|" + (IsWorkspace ? "workspace|" : "folder|") + WorkspaceUri;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProjectIdentity) || string.IsNullOrWhiteSpace(Name) ||
            string.IsNullOrWhiteSpace(Environment) || string.IsNullOrWhiteSpace(Executable) ||
            string.IsNullOrWhiteSpace(WorkspaceUri) || !Path.IsPathFullyQualified(Executable) ||
            !(WorkspaceUri.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) ||
              WorkspaceUri.StartsWith("vscode-remote://", StringComparison.OrdinalIgnoreCase)) ||
            WorkspaceUri.Any(char.IsControl))
            throw new FormatException("Invalid recent project configuration.");
    }
}

public sealed class ConfigurationUsage
{
    public required ProjectConfiguration Configuration { get; set; }
    public long ForegroundTicks { get; set; }
    public DateTimeOffset LastAccessed { get; set; }
}

public sealed class RecentProjects
{
    private readonly Dictionary<string, ConfigurationUsage> configurations = new(StringComparer.Ordinal);
    private string? previousForeground;
    public bool IsDirty { get; private set; }
    public IReadOnlyCollection<ConfigurationUsage> Configurations => configurations.Values;

    public RecentProjects(IEnumerable<ConfigurationUsage> saved)
    {
        foreach (var usage in saved)
        {
            if (usage?.Configuration is null || usage.ForegroundTicks < 0 ||
                usage.LastAccessed == default)
                throw new FormatException("Invalid recent project usage history.");
            usage.Configuration.Validate();
            if (!configurations.TryAdd(usage.Configuration.Id, usage))
                throw new FormatException("Duplicate recent project configuration.");
        }
    }

    public void Observe(IEnumerable<ProjectConfiguration> open, string? foreground, DateTimeOffset now, TimeSpan elapsed)
    {
        foreach (var configuration in open)
        {
            configuration.Validate();
            if (configurations.TryGetValue(configuration.Id, out var existing))
            {
                if (existing.Configuration != configuration)
                {
                    existing.Configuration = configuration;
                    IsDirty = true;
                }
            }
            else
            {
                configurations.Add(configuration.Id, new ConfigurationUsage
                {
                    Configuration = configuration, LastAccessed = now
                });
                IsDirty = true;
            }
        }
        if (foreground is not null && configurations.TryGetValue(foreground, out var active))
        {
            // Do not count suspension, debugger pauses, or time between application runs.
            if (foreground == previousForeground && elapsed > TimeSpan.Zero && elapsed <= TimeSpan.FromSeconds(5))
                active.ForegroundTicks = checked(active.ForegroundTicks + elapsed.Ticks);
            active.LastAccessed = now;
            IsDirty = true;
        }
        previousForeground = foreground;
    }

    public IReadOnlyList<ProjectConfiguration> MenuEntries() =>
        configurations.Values.GroupBy(usage => usage.Configuration.ProjectIdentity, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Max(usage => usage.LastAccessed))
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .Select(group => group.OrderByDescending(usage => usage.ForegroundTicks)
                .ThenByDescending(usage => usage.LastAccessed)
                .ThenBy(usage => usage.Configuration.Id, StringComparer.Ordinal)
                .First().Configuration)
            .ToArray();

    public void MarkSaved() => IsDirty = false;

    public IReadOnlyList<ProjectConfiguration> OrderedMenuEntries(IReadOnlyList<RelativeOrder> relations,
        IEnumerable<string> openOrder)
    {
        var entries = MenuEntries();
        var identities = openOrder
            .Concat(configurations.Values.Select(usage =>
                usage.Configuration.ButtonOrderIdentity ?? usage.Configuration.ProjectIdentity))
            .Concat(relations.SelectMany(relation => new[] { relation.Before, relation.After }))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var order = Identity.Sort(identities, relations, identity => identity)
            .Select((identity, index) => (identity, index))
            .ToDictionary(entry => entry.identity, entry => entry.index, StringComparer.OrdinalIgnoreCase);
        return entries.OrderBy(configuration =>
                order[configuration.ButtonOrderIdentity ?? configuration.ProjectIdentity])
            .ThenBy(configuration => configuration.ProjectIdentity, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
