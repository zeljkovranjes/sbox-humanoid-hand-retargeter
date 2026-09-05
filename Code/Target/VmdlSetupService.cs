#nullable enable
namespace HumanoidHandRetargeter.Target;

public sealed record HandAnimationEntry(string Name, string SourceFilename, bool Looping = false);
public sealed record ConfiguredAnimation(string RequestedName, string SequenceName, string SourceFilename);

public sealed class VmdlSetupOptions
{
    public required string ModelPath { get; init; }
    public required string BindPoseSource { get; init; }
    public string BindPoseName { get; init; } = "bindPose";
    public bool AutoConfigureAnimGraph { get; init; } = true;
    /// <summary>Arms bonemerge onto a weapon; this service must not assign a weapon graph to arms.</summary>
    public bool WeaponCompatibleArms { get; init; }
}

/// <summary>Reviewable, pure setup output. File persistence and asset compilation belong to the editor.</summary>
public sealed record VmdlSetupResult(string VmdlText, bool Changed, bool BindPoseAdded,
    string? GeneratedGraphPath, string? GeneratedGraphText,
    IReadOnlyList<ConfiguredAnimation> Animations, IReadOnlyList<string> Changes);

/// <summary>Hand-specific specialization of the audited VmdlAugmenter. Uses its KV3 tree
/// representation to preserve unknown settings and avoid editing ModelDoc text in UI code.</summary>
public static class VmdlSetupService
{
    public static VmdlSetupResult Prepare(string existingVmdl, IEnumerable<HandAnimationEntry> animations,
        VmdlSetupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(animations);
        var modelPath = NormalizeAssetPath(options.ModelPath, ".vmdl");
        var bindSource = NormalizeAssetPath(options.BindPoseSource, ".dmx");
        ValidateName(options.BindPoseName);
        var entries = animations.ToArray();
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ValidateName(entry.Name);
            NormalizeAssetPath(entry.SourceFilename, ".dmx");
        }
        if (entries.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Length)
            throw new ArgumentException("The batch contains duplicate animation names.", nameof(animations));
        if (entries.Any(e => string.Equals(e.Name, options.BindPoseName, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The bind-pose name is reserved; rename the input animation.", nameof(animations));

        var doc = Kv3.Parse(existingVmdl);
        if (doc.Root is not KvObject document || document.GetOrNull("rootNode") is not KvObject root
            || root.GetString("_class") != "RootNode")
            throw new FormatException("VMDL has no ModelDoc RootNode.");
        var changes = new List<string>();
        var rootChildren = Children(root);
        var lists = rootChildren.Items.OfType<KvObject>().Where(n => n.GetString("_class") == "AnimationList").ToArray();
        if (lists.Length > 1) throw new FormatException("VMDL has multiple AnimationLists; resolve this before setup.");
        var list = lists.FirstOrDefault();
        if (list is null)
        {
            list = new KvObject { ["_class"] = new KvString("AnimationList"), ["children"] = new KvArray(),
                ["default_root_bone_name"] = new KvString("") };
            rootChildren.Items.Add(list);
            changes.Add("Added AnimationList.");
        }
        var nodes = SequenceNodes(Children(list)).ToList();
        var names = new Dictionary<string, KvObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
            if (node.GetString("name") is { Length: > 0 } name && !names.TryAdd(name, node))
                throw new FormatException($"VMDL contains duplicate sequence name '{name}'.");
        var configured = new List<ConfiguredAnimation>();
        var bindAdded = false;
        string bindName;
        if (names.TryGetValue(options.BindPoseName, out var existingBind)
            && existingBind.GetString("_class") == "AnimFile")
        {
            bindName = options.BindPoseName;
            if (string.IsNullOrWhiteSpace(existingBind.GetString("source_filename")))
                throw new FormatException("The existing bind pose has no source file.");
        }
        else
        {
            var before = names.Count;
            bindName = Upsert(new(options.BindPoseName, bindSource));
            bindAdded = names.Count > before;
        }
        foreach (var entry in entries)
            configured.Add(new(entry.Name, Upsert(entry), NormalizeAssetPath(entry.SourceFilename, ".dmx")));

        string? graphPath = null, graphText = null;
        var existingGraph = root.GetOrNull("anim_graph_name");
        if (existingGraph is not null && existingGraph is not KvString)
            throw new FormatException("VMDL anim_graph_name must be a string.");
        if (options.AutoConfigureAnimGraph && !options.WeaponCompatibleArms
            && string.IsNullOrWhiteSpace(root.GetString("anim_graph_name")))
        {
            graphPath = modelPath[..^5] + "_hands.vanmgrph";
            var idle = entries.FirstOrDefault(e => e.Looping && e.Name.Contains("idle", StringComparison.OrdinalIgnoreCase)
                && !e.Name.EndsWith("_delta", StringComparison.OrdinalIgnoreCase));
            var initialSequence = idle is null ? bindName : configured.Single(c => c.RequestedName == idle.Name).SequenceName;
            graphText = HandAnimGraph.Create(initialSequence, modelPath);
            root["anim_graph_name"] = new KvString(graphPath);
            changes.Add($"Assigned standalone hand graph '{graphPath}' using '{initialSequence}'.");
        }
        var changed = changes.Count > 0;
        return new(changed ? Kv3.Serialize(doc) : existingVmdl, changed, bindAdded,
            graphPath, graphText, configured.AsReadOnly(), changes.AsReadOnly());

        string Upsert(HandAnimationEntry entry)
        {
            var path = NormalizeAssetPath(entry.SourceFilename, ".dmx");
            var name = entry.Name;
            var suffix = 0;
            while (names.TryGetValue(name, out var occupied)
                && (occupied.GetString("_class") != "AnimFile"
                    || !string.Equals(occupied.GetString("source_filename")?.Replace('\\', '/'), path, StringComparison.OrdinalIgnoreCase)))
                name = entry.Name + "_retargeted" + (++suffix == 1 ? "" : "_" + suffix);
            if (names.TryGetValue(name, out var current))
            {
                // Update only the setting owned by this request. Preserve events, fades,
                // compression, filters, frame ranges and other authored children.
                if (current.GetOrNull("looping") is not KvBool loop || loop.Value != entry.Looping)
                {
                    current["looping"] = new KvBool(entry.Looping);
                    changes.Add($"Updated looping for '{name}'.");
                }
            }
            else
            {
                var node = BuildAnimFile(name, path, entry.Looping);
                Children(list).Items.Add(node);
                names.Add(name, node);
                changes.Add($"Added sequence '{name}'.");
            }
            return name;
        }
    }

    private static KvArray Children(KvObject node)
    {
        if (node.GetOrNull("children") is KvArray children) return children;
        if (node.GetOrNull("children") is not null) throw new FormatException("ModelDoc children must be an array.");
        var result = new KvArray();
        node["children"] = result;
        return result;
    }

    private static IEnumerable<KvObject> SequenceNodes(KvArray children)
    {
        foreach (var node in children.Items.OfType<KvObject>())
        {
            if (node.GetString("_class") == "Folder")
            {
                foreach (var child in SequenceNodes(Children(node))) yield return child;
            }
            else yield return node;
        }
    }

    /// <summary>Normalizes and validates an Assets-relative reference for setup and editor IO.</summary>
    public static string NormalizeAssetPath(string path, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = path.Replace('\\', '/');
        if (path.StartsWith('/') || path.Contains(':') || path.Any(char.IsControl)
            || path.Split('/').Any(p => p is "" or "." or "..")
            || !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Expected an assets-relative {extension} path.", nameof(path));
        return path;
    }

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Any(char.IsControl)) throw new ArgumentException("Sequence names cannot contain control characters.");
    }

    // Same AnimFile defaults as the audited VmdlWriter.BuildAnimFileNode.
    private static KvObject BuildAnimFile(string name, string path, bool looping)
        => new()
        {
            ["_class"] = new KvString("AnimFile"), ["name"] = new KvString(name),
            ["activity_name"] = new KvString(""), ["activity_weight"] = new KvLong(1),
            ["weight_list_name"] = new KvString(""), ["fade_in_time"] = new KvDouble(.2),
            ["fade_out_time"] = new KvDouble(.2), ["looping"] = new KvBool(looping),
            ["delta"] = new KvBool(false), ["worldSpace"] = new KvBool(false), ["hidden"] = new KvBool(false),
            ["anim_markup_ordered"] = new KvBool(false), ["disable_compression"] = new KvBool(false),
            ["disable_interpolation"] = new KvBool(false), ["enable_scale"] = new KvBool(false),
            ["source_filename"] = new KvString(path), ["start_frame"] = new KvLong(-1),
            ["end_frame"] = new KvLong(-1), ["framerate"] = new KvDouble(-1),
            ["take"] = new KvLong(0), ["reverse"] = new KvBool(false)
        };
}
