#nullable enable
namespace HumanoidHandRetargeter.Formats.Fbx;

/// <summary>Selects skinned target meshes without importing loose scene props as hands.</summary>
public static class FbxSkinnedMeshes
{
    public static IReadOnlyList<string> ReadNames(byte[] bytes)
    {
        var root=FbxTokenizer.Parse(bytes);
        var objects=root.Child("Objects")?.Children??new List<FbxNode>();
        var links=root.Child("Connections")?.ChildrenNamed("C")
            .Where(n=>n.Properties.Count>=3&&n.Prop<string>(0)=="OO").ToArray()??Array.Empty<FbxNode>();
        var skins=objects.Where(n=>n.Name=="Deformer"&&n.Properties.Count>=3&&n.Prop<string>(2)=="Skin").Select(n=>n.Prop<long>(0)).ToHashSet();
        var geometry=links.Where(n=>skins.Contains(n.Prop<long>(1))).Select(n=>n.Prop<long>(2)).ToHashSet();
        var models=links.Where(n=>geometry.Contains(n.Prop<long>(1))).Select(n=>n.Prop<long>(2)).ToHashSet();
        // Avoid introducing an unnecessary name filter when every mesh is skinned.
        if(objects.Where(n=>n.Name=="Model"&&n.Properties.Count>=3&&n.Prop<string>(2)=="Mesh")
            .All(n=>models.Contains(n.Prop<long>(0))))return Array.Empty<string>();
        return objects.Where(n=>n.Name=="Model"&&models.Contains(n.Prop<long>(0)))
            .Select(n=>FbxNode.SplitName(n.Prop<string>(1)).Name)
            .SelectMany(name=>new[]{name,name[(name.LastIndexOf(':')+1)..]})
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
