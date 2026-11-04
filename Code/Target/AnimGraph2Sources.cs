#nullable enable
namespace HumanoidHandRetargeter.Target;

/// <summary>The source files an AnimGraph2 graph plays from, as KV3 text (their compiler rejects JSON): a skeleton
/// (<c>.skel</c>) listing the bones of the source art it names, and a clip (<c>.anim</c>) per animation, compiled
/// against that skeleton. A clip only plays in a graph whose skeleton it was compiled against.</summary>
public static class AnimGraph2Sources
{
    public const string Header = "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->";

    /// <summary>A <c>.skel</c> with the bones of <paramref name="sourceFile"/> (an assets-relative FBX, DMX or SMD), scaled
    /// by <paramref name="scale"/>: these sources don't pass through a model's scale modifiers, so they take its scale.
    /// Clips compiled against the skeleton are scaled by it too.</summary>
    public static string Skeleton(string sourceFile, double scale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
        ValidateScale(scale);
        return Kv3.Serialize(new Kv3Document(Header, new KvObject
        {
            ["m_sourceFilename"] = new KvString(sourceFile), ["m_flGlobalScale"] = new KvDouble(scale),
            ["m_additionalSourceFilenames"] = new KvArray(), ["m_rootBoneName"] = new KvString("")
        }));
    }

    /// <summary>An <c>.anim</c> playing <paramref name="sourceFile"/>'s first take on <paramref name="skeleton"/> (a <c>.skel</c>),
    /// in the skeleton's scale.</summary>
    public static string Clip(string sourceFile, string skeleton)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(skeleton);
        return Kv3.Serialize(new Kv3Document(Header, new KvObject
        {
            ["_class"] = new KvString("CClipDocument"), ["m_sourceFilename"] = new KvString(sourceFile),
            ["m_animationSkeletonName"] = new KvString(skeleton), ["m_nTake"] = new KvLong(0), ["m_flImportScale"] = new KvDouble(1)
        }));
    }

    /// <summary>The scale a ModelDoc document applies to its animation sources: the product of its scale modifiers.</summary>
    public static double ModelScale(KvValue document)
    {
        var scale = 1.0;
        void Walk(KvValue value)
        {
            if (value is KvObject node)
            {
                if (node.GetString("_class") == "ModelModifier_ScaleAndMirror" && node.GetOrNull("scale") is KvDouble factor) scale *= factor.Value;
                foreach (var key in node.Keys) Walk(node[key]);
            }
            else if (value is KvArray array) foreach (var item in array.Items) Walk(item);
        }
        Walk(document);
        ValidateScale(scale);
        return scale;
    }

    private static void ValidateScale(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale), scale, "An AnimGraph2 source scale must be finite and positive.");
    }

    /// <summary>The path of the AnimGraph2 file of <paramref name="extension"/> (<c>.skel</c>, <c>.anim</c>) beside a source file.</summary>
    public static string Beside(string sourceFile, string extension)
        => sourceFile[..sourceFile.LastIndexOf('.')] + extension;
}
