using System.Text;
using System.Text.Json;
using VSCDeck.App;
using VSCDeck.Core;

namespace VSCDeck.WindowsTests;

internal static partial class Program
{
    private static int RunMruDiscoveryTests()
    {
        try
        {
            var before = NativeWindows.Discover();
            var warnings = new List<string>();
            var windows = new ProjectConfigurationCatalog().Attach(before, warnings);
            foreach (var window in windows)
                Console.WriteLine($"{window.Project}: {window.Configuration?.Environment ?? "unresolved"}; " +
                    $"project={window.Configuration?.ProjectIdentity}; target={window.Configuration?.WorkspaceUri}");
            foreach (var warning in warnings) Console.WriteLine("WARNING: " + warning);
            Check(windows.Count == before.Count &&
                before.Select(window => window.Handle).ToHashSet().SetEquals(
                    NativeWindows.Discover().Select(window => window.Handle)),
                "Configuration discovery preserves all existing VS Code windows");
            Console.WriteLine($"Resolved {windows.Count(window => window.Configuration is not null)} of {windows.Count} open windows without activating VS Code.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int RunMruTests()
    {
        try
        {
            const string executable = @"C:\VS Code\Code.exe";
            var local = ProjectConfigurationCatalog.ParseLocation(new Uri(@"C:\work\Project").AbsoluteUri, false, executable);
            var metadata = JsonSerializer.Serialize(new
            {
                hostPath = @"C:\work\Project", settings = new { context = "desktop-linux" },
                configFile = new { path = "/c:/work/Project/.devcontainer/devcontainer.json" }
            });
            var uri = "vscode-remote://dev-container%2B" +
                Convert.ToHexString(Encoding.UTF8.GetBytes(metadata)) + "/workspaces/Project";
            var container = ProjectConfigurationCatalog.ParseLocation(uri, false, executable);
            Check(local.Configuration.ProjectIdentity == container.Configuration.ProjectIdentity,
                "Local and container configurations share the host project identity");
            Check(local.Configuration.Id != container.Configuration.Id && container.Configuration.WorkspaceUri == uri,
                "Exact container configuration and reopen authority remain separate from local");
            Check(ProjectConfigurationCatalog.Matches(container,
                new(1, 0, "", "Project [Dev Container: Test @ desktop-linux]", null)) &&
                !ProjectConfigurationCatalog.Matches(container, new(1, 0, "", "Project", null)),
                "Container matching includes the actual remote environment");
            var ssh = ProjectConfigurationCatalog.ParseLocation(
                "vscode-remote://ssh-remote%2Bdevbox/home/me/Project", false, executable);
            var remoteMetadata = Convert.ToHexString(Encoding.UTF8.GetBytes(
                """{"hostPath":"/home/me/Project","configFile":{"path":"container.json"}}"""));
            var remoteContainer = ProjectConfigurationCatalog.ParseLocation(
                $"vscode-remote://dev-container%2B{remoteMetadata}@ssh-remote%2Bdevbox/workspaces/Project", false, executable);
            Check(ssh.Configuration.ProjectIdentity == remoteContainer.Configuration.ProjectIdentity,
                "SSH and container over SSH share the remote project identity");
            var containerWindow = new CodeWindow(1, 0, "", "Project [Dev Container: Test @ devbox]", null)
            {
                ProjectIdentity = @"docker:devbox:\home\me\project"
            };
            var remoteSource = new RemoteSource
            {
                Project = "Project", Workspace = "/home/me/Project", SshHost = "devbox"
            };
            Check(ProjectConfigurationCatalog.ApplyProjectIdentity(remoteContainer.Configuration,
                containerWindow, [remoteSource]).ProjectIdentity == ssh.Configuration.ProjectIdentity,
                "An implicit ordering identity does not split SSH from container-over-SSH usage");
            remoteSource.ProjectIdentity = @"C:\work\Project";
            Check(ProjectConfigurationCatalog.ApplyProjectIdentity(remoteContainer.Configuration,
                containerWindow, [remoteSource]).ProjectIdentity == local.Configuration.ProjectIdentity,
                "Explicit project mappings can group local and remote configurations");
            Check(ProjectConfigurationCatalog.Matches(ssh, new(1, 0, "", "Project [SSH: devbox]", null)) &&
                !ProjectConfigurationCatalog.Matches(ssh, new(1, 0, "", "Project [SSH: other]", null)),
                "SSH matching preserves host identity");
            var otherMetadata = Convert.ToHexString(Encoding.UTF8.GetBytes(
                """{"hostPath":"C:\\work\\Project","settings":{"context":"desktop-linux"},"configFile":{"path":"other.json"}}"""));
            var other = ProjectConfigurationCatalog.ParseLocation(
                $"vscode-remote://dev-container%2B{otherMetadata}/workspaces/Project", false, executable);
            Check(other.Configuration.Id != container.Configuration.Id, "Different container configuration files remain distinct");
            var workspace = ProjectConfigurationCatalog.ParseLocation(
                new Uri(@"C:\work\My Project.code-workspace").AbsoluteUri, true, executable);
            var launch = ProjectConfigurationCatalog.CreateLaunch(workspace.Configuration);
            Check(launch.FileName == executable && !launch.UseShellExecute &&
                launch.ArgumentList.SequenceEqual(["--new-window", "--file-uri", workspace.Configuration.WorkspaceUri]),
                "Workspace launch uses separated exact URI arguments and a new window");
            launch = ProjectConfigurationCatalog.CreateLaunch(remoteContainer.Configuration);
            Check(launch.ArgumentList.SequenceEqual(["--new-window", "--folder-uri", remoteContainer.Configuration.WorkspaceUri]),
                "Remote folder launch preserves the exact URI and requests a new window");
            var open = new CodeWindow(42, 0, "", "Project", null) { Configuration = container.Configuration };
            Check(ProjectConfigurationCatalog.FindOpenConfiguration(container.Configuration, [open]) == (nint)42,
                "An open exact configuration routes to existing-window activation");
            Check(ProjectConfigurationCatalog.FindOpenConfiguration(local.Configuration, [open]) is null,
                "Another configuration of the same project does not prevent launching");
            var rejected = false;
            try { ProjectConfigurationCatalog.FindOpenConfiguration(local.Configuration, [new(3, 0, "", "Project", null)]); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Unresolved open windows are reported rather than silently duplicated");
            var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var history = new RecentProjects([]);
            var configs = new[] { local.Configuration, container.Configuration };
            history.Observe(configs, local.Configuration.Id, now, TimeSpan.Zero);
            history.Observe(configs, local.Configuration.Id, now.AddSeconds(1), TimeSpan.FromSeconds(1));
            history.Observe(configs, container.Configuration.Id, now.AddSeconds(2), TimeSpan.FromSeconds(1));
            history.Observe(configs, container.Configuration.Id, now.AddSeconds(3), TimeSpan.FromSeconds(1));
            history.Observe(configs, container.Configuration.Id, now.AddSeconds(4), TimeSpan.FromSeconds(1));
            Check(history.MenuEntries().Single() == container.Configuration, "Longest-used configuration is the single entry for a project");
            history.Observe(configs, local.Configuration.Id, now.AddSeconds(5), TimeSpan.FromSeconds(1));
            Check(history.MenuEntries().Single() == container.Configuration, "Most recent use does not override longest use");
            var before = history.Configurations.Sum(usage => usage.ForegroundTicks);
            history.Observe(configs, null, now.AddSeconds(6), TimeSpan.FromSeconds(1));
            history.Observe(configs, null, now.AddHours(1), TimeSpan.FromHours(1));
            Check(history.Configurations.Sum(usage => usage.ForegroundTicks) == before, "Background and suspended intervals do not accrue use");
            history.Observe(configs, local.Configuration.Id, now.AddHours(2), TimeSpan.FromHours(1));
            Check(history.Configurations.Sum(usage => usage.ForegroundTicks) == before, "Long sampling gaps do not count as usage");
            for (var index = 0; index < 22; index++)
            {
                var configuration = local.Configuration with
                {
                    Name = $"P{index}", ProjectIdentity = $"p{index}",
                    WorkspaceUri = new Uri($@"C:\work\P{index}").AbsoluteUri
                };
                history.Observe([configuration], configuration.Id, now.AddHours(3).AddMinutes(index), TimeSpan.Zero);
                if (index == 18)
                    Check(history.MenuEntries().Count == 20 &&
                        history.MenuEntries().Any(entry => entry == container.Configuration),
                        "Exactly twenty project identities are displayed without dropping the oldest project");
            }
            Check(history.MenuEntries().Select(configuration => configuration.Name).SequenceEqual(
                    Enumerable.Range(2, 20).Reverse().Select(index => $"P{index}")) &&
                history.Configurations.Count == 24,
                "Only the twenty most recent projects are displayed while all configurations are retained");
            var restored = new RecentProjects(JsonSerializer.Deserialize<List<ConfigurationUsage>>(
                JsonSerializer.Serialize(history.Configurations))!);
            Check(restored.MenuEntries().SequenceEqual(history.MenuEntries()) &&
                restored.Configurations.Sum(usage => usage.ForegroundTicks) == history.Configurations.Sum(usage => usage.ForegroundTicks),
                "All durations, configurations, and ordering survive serialization");
            history.Observe(configs, local.Configuration.Id, now.AddDays(1), TimeSpan.Zero);
            Check(history.MenuEntries()[0] == container.Configuration, "An evicted project returns with its most-used configuration");
            var ticks = restored.Configurations.Sum(usage => usage.ForegroundTicks);
            restored.Observe(configs, local.Configuration.Id, now.AddDays(2), TimeSpan.FromSeconds(1));
            Check(restored.Configurations.Sum(usage => usage.ForegroundTicks) == ticks, "Restart does not charge time from the previous process");
            history.MarkSaved();
            Check(!history.IsDirty, "Saving clears the dirty flag");
            var ordered = history.OrderedMenuEntries(Identity.RelationsFor(
                Enumerable.Range(0, 22).Select(index => $"p{index}")), []);
            Check(ordered.Select(configuration => configuration.Name).SequenceEqual(
                Enumerable.Range(3, 19).Select(index => $"P{index}").Append("Project")),
                "The twenty eligible projects follow saved bar order rather than recency");
            var pair = new RecentProjects([
                new() { Configuration = local.Configuration with { ButtonOrderIdentity = "a" }, LastAccessed = now },
                new() { Configuration = other.Configuration with { ProjectIdentity = "other", ButtonOrderIdentity = "c" },
                    LastAccessed = now.AddSeconds(1) }
            ]);
            Check(pair.OrderedMenuEntries([new("a", "b"), new("b", "c")], [])
                .Select(configuration => configuration.ButtonOrderIdentity).SequenceEqual(["a", "c"]),
                "Closed intermediate projects preserve transitive bar relationships");
            Check(pair.OrderedMenuEntries([], ["c", "a"])
                .Select(configuration => configuration.ButtonOrderIdentity).SequenceEqual(["c", "a"]),
                "Unordered entries follow the current bar order");
            var tied = new RecentProjects([
                new() { Configuration = local.Configuration, ForegroundTicks = 10, LastAccessed = now },
                new() { Configuration = container.Configuration, ForegroundTicks = 10, LastAccessed = now.AddSeconds(1) }
            ]);
            Check(tied.MenuEntries().Single() == container.Configuration, "Equal duration prefers the most recent configuration");
            rejected = false;
            try { _ = new RecentProjects([new() { Configuration = local.Configuration, ForegroundTicks = -1, LastAccessed = now }]); }
            catch (FormatException) { rejected = true; }
            Check(rejected, "Invalid negative usage history is rejected explicitly");
            Console.WriteLine($"All {checks} MRU checks passed without opening or activating VS Code.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
