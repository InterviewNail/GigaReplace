namespace GigaReplace.Core.Analysis;

/// <summary>What the whole tree declares and serializes, used to judge each match.</summary>
public sealed class SolutionIndex
{
    internal HashSet<string> Declared { get; } = new(StringComparer.Ordinal);

    /// <summary>Words from files that aren't parsed (Razor, VB, .proto …); they only suppress "not declared" flags.</summary>
    internal HashSet<string> WeakDeclared { get; } = new(StringComparer.Ordinal);

    internal HashSet<string> TypeNames { get; } = new(StringComparer.Ordinal);

    /// <summary>Names of all type members (methods, properties, fields, …) anywhere in the tree.</summary>
    internal HashSet<string> MemberNames { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, HashSet<string>> TypesByNamespace { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, HashSet<string>> MembersByType { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, HashSet<string>> LocalsByType { get; } = new(StringComparer.Ordinal);

    /// <summary>Simple type name → why it looks serialized.</summary>
    internal Dictionary<string, string> SerializedTypes { get; } = new(StringComparer.Ordinal);

    internal HashSet<string> XmlContractTypes { get; } = new(StringComparer.Ordinal);

    internal HashSet<string> SettingsTypes { get; } = new(StringComparer.Ordinal);

    internal HashSet<string> StringEnums { get; } = new(StringComparer.Ordinal);

    internal bool UsesStringEnumConverter { get; private set; }

    internal Dictionary<string, ProjectFacts> Projects { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Assembly names and package IDs built by projects in the tree.</summary>
    internal HashSet<string> ProducedNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal Dictionary<string, List<PathReference>> PathReferences { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IdentityGuidCandidate> IdentityGuids { get; private set; } = [];

    internal bool IsDeclared(string name) => Declared.Contains(name) || WeakDeclared.Contains(name);

    internal bool TypeExists(string ns, string name) => TypesByNamespace.TryGetValue(ns, out var set) && set.Contains(name);

    internal bool MemberExists(string typeKey, string name) => MembersByType.TryGetValue(typeKey, out var set) && set.Contains(name);

    internal bool LocalExists(string typeKey, string name) => LocalsByType.TryGetValue(typeKey, out var set) && set.Contains(name);

    internal string SerializedReason(string type) => SerializedTypes.TryGetValue(type, out var reason) ? reason : "serialized";

    internal static SolutionIndex Build(IEnumerable<(string Path, FileFacts Facts)> files)
    {
        var index = new SolutionIndex();
        var memberTypes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var guids = new Dictionary<string, (SortedSet<string> Labels, string First)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, f) in files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            index.Declared.UnionWith(f.Declared);
            index.WeakDeclared.UnionWith(f.WeakDeclared);
            index.TypeNames.UnionWith(f.TypeNames);
            foreach (var (ns, type) in f.Types)
            {
                Add(index.TypesByNamespace, ns, type);
            }

            foreach (var (typeKey, member) in f.Members)
            {
                Add(index.MembersByType, typeKey, member);
                index.MemberNames.Add(member);
            }

            foreach (var (typeKey, local) in f.Locals)
            {
                Add(index.LocalsByType, typeKey, local);
            }

            foreach (var (type, reason) in f.Serialized)
            {
                index.SerializedTypes.TryAdd(type, reason);
            }

            foreach (var (type, memberType) in f.MemberTypes)
            {
                Add(memberTypes, type, memberType);
            }

            index.XmlContractTypes.UnionWith(f.XmlContract);
            index.SettingsTypes.UnionWith(f.SettingsTypes);
            index.StringEnums.UnionWith(f.StringEnums);
            index.UsesStringEnumConverter |= f.UsesStringEnumConverter;

            if (f.Project is { } project)
            {
                index.Projects[project.RelativePath] = project;
                index.ProducedNames.Add(project.AssemblyName ?? project.FileBaseName);
                index.ProducedNames.Add(project.PackageId ?? project.AssemblyName ?? project.FileBaseName);
            }

            foreach (var candidate in f.Guids)
            {
                if (!guids.TryGetValue(candidate.Guid, out var entry))
                {
                    guids[candidate.Guid] = entry = (new SortedSet<string>(StringComparer.Ordinal), candidate.FirstFile);
                }

                entry.Labels.Add(candidate.Label);
            }

            if (f.PathReferences.Count > 0)
            {
                index.PathReferences[path] = f.PathReferences;
            }
        }

        foreach (var settingsType in index.SettingsTypes)
        {
            index.SerializedTypes.TryAdd(settingsType, "user settings (ApplicationSettingsBase)");
        }

        // A type used by a serialized type's members is serialized too.
        var queue = new Queue<string>(index.SerializedTypes.Keys);
        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (!memberTypes.TryGetValue(type, out var used))
            {
                continue;
            }

            foreach (var memberType in used)
            {
                if (index.TypeNames.Contains(memberType)
                    && index.SerializedTypes.TryAdd(memberType, $"stored inside {type}, which looks serialized"))
                {
                    queue.Enqueue(memberType);
                }
            }
        }

        index.IdentityGuids = guids
            .Select(g => new IdentityGuidCandidate(g.Key.ToLowerInvariant(), string.Join(", ", g.Value.Labels), g.Value.First))
            .OrderBy(g => g.FirstFile, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Label, StringComparer.Ordinal)
            .ToList();

        return index;
    }

    private static void Add(Dictionary<string, HashSet<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var set))
        {
            map[key] = set = new HashSet<string>(StringComparer.Ordinal);
        }

        set.Add(value);
    }
}
