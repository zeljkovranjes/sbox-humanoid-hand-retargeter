#nullable enable
namespace HumanoidHandRetargeter.Target;

/// <summary>Minimal hand ModelDoc using the audited VmdlWriter mesh and scaling node shapes.</summary>
public static class HandModelFactory
{
    public const string Header = "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc30:version{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1} -->";
    public static string Create(string baseModel = "", string mesh = "", float meshUnitScaleCm = 1, IReadOnlyDictionary<string,string>? materialRemaps = null, IReadOnlyList<string>? meshNames = null)
    {
        if (!float.IsFinite(meshUnitScaleCm) || meshUnitScaleCm <= 0) throw new ArgumentOutOfRangeException(nameof(meshUnitScaleCm));
        if (string.IsNullOrWhiteSpace(baseModel) == string.IsNullOrWhiteSpace(mesh)) throw new ArgumentException("Choose one base model or mesh source.");
        var children = new KvArray();
        if (mesh.Length > 0)
        {
            mesh = VmdlSetupService.NormalizeAssetPath(mesh, ".fbx");
            var meshes = new KvArray();
            meshes.Items.Add(new KvObject { ["_class"] = new KvString("RenderMeshFile"), ["name"] = new KvString("hands_mesh"),
                ["filename"] = new KvString(mesh), ["import_scale"] = new KvDouble(meshUnitScaleCm),
                ["import_translation"] = Vector(0,0,0), ["import_rotation"] = Vector(0,0,0),
                ["align_origin_x_type"] = new KvString("None"), ["align_origin_y_type"] = new KvString("None"), ["align_origin_z_type"] = new KvString("None") });
            if(meshNames is {Count:>0})
            {
                var names=new KvArray();foreach(var name in meshNames)names.Items.Add(new KvString(name));
                ((KvObject)meshes.Items[0])["import_filter"]=new KvObject
                    {["exclude_by_default"]=new KvBool(true),["exception_list"]=names};
            }
            children.Items.Add(new KvObject { ["_class"] = new KvString("RenderMeshList"), ["children"] = meshes });
            children.Items.Add(new KvObject { ["_class"] = new KvString("BoneMarkupList"), ["bone_cull_type"] = new KvString("None"), ["children"] = new KvArray() });
            var remaps=new KvArray();
            if(materialRemaps is not null)foreach(var pair in materialRemaps)remaps.Items.Add(new KvObject{["from"]=new KvString(pair.Key),["to"]=new KvString(pair.Value)});
            var materials = new KvArray();
            materials.Items.Add(new KvObject { ["_class"] = new KvString("DefaultMaterialGroup"), ["use_global_default"] = new KvBool(materialRemaps is null || materialRemaps.Count==0),
                ["global_default_material"] = new KvString("materials/dev/reflectivity_50.vmat"), ["remaps"] = remaps });
            children.Items.Add(new KvObject { ["_class"] = new KvString("MaterialGroupList"), ["children"] = materials });
        }
        else baseModel = VmdlSetupService.NormalizeAssetPath(baseModel, ".vmdl");
        var modifiers = new KvArray();
        modifiers.Items.Add(new KvObject { ["_class"] = new KvString("ModelModifier_ScaleAndMirror"), ["scale"] = new KvDouble(1.0 / 2.54),
            ["mirror_x"] = new KvBool(false), ["mirror_y"] = new KvBool(false), ["mirror_z"] = new KvBool(false),
            ["flip_bone_forward"] = new KvBool(false), ["swap_left_and_right_bones"] = new KvBool(false) });
        children.Items.Add(new KvObject { ["_class"] = new KvString("ModelModifierList"), ["children"] = modifiers });
        var root = new KvObject { ["_class"] = new KvString("RootNode"), ["children"] = children, ["base_model_name"] = new KvString(baseModel),
            ["anim_graph_name"] = new KvString(""), ["model_archetype"] = new KvString(""), ["primary_associated_entity"] = new KvString("") };
        return Kv3.Serialize(new Kv3Document(Header, new KvObject { ["rootNode"] = root }));
    }

    private static KvArray Vector(params double[] values) { var array = new KvArray(); foreach (var value in values) array.Items.Add(new KvDouble(value)); return array; }
}
