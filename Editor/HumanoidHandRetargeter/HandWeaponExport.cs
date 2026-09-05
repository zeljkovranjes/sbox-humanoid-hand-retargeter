#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HumanoidHandRetargeter.Formats.Dmx;
using HumanoidHandRetargeter.Skeleton;
using HumanoidHandRetargeter.Target;
using Sandbox;
using System.Text.Json.Nodes;

namespace HumanoidHandRetargeter.Editor;

/// <summary>Builds immutable per-weapon animation owners; the original weapon supplies its mesh and materials.</summary>
internal static class HandWeaponExport
{
    internal sealed record Bundle(string ModelPath,string GraphPath,string WeaponPath,string HandsPath,
        IReadOnlyList<WeaponGraphClip> Actions,int BoneCount,global::Vector3 ViewOrigin)
    {
        public string PrefabPath=>ModelPath[..ModelPath.LastIndexOf('/')]+"/weapon.prefab";
    }

    public static string Prefab(Bundle bundle)
    {
        var scene=Scene.CreateEditorScene();
        try
        {
            using var scope=scene.Push();
            var go=new GameObject(false,"Retargeted "+System.IO.Path.GetFileNameWithoutExtension(bundle.WeaponPath));
            var component=go.GetOrAddComponent<RetargetedWeapon>();
            component.WeaponModel=Model.Load(bundle.WeaponPath);component.HandsModel=Model.Load(bundle.HandsPath);
            component.ViewOrigin=bundle.ViewOrigin;
            var json=go.Serialize();json["Enabled"]=true;
            string GuidFor(string suffix)=>new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(bundle.ModelPath+suffix)).Take(16).ToArray()).ToString();
            json["__guid"]=GuidFor("object");
            var data=((JsonArray)json["Components"]!).OfType<JsonObject>().Single();
            data["__guid"]=GuidFor("component");data["AnimationModel"]=bundle.ModelPath;
            var prefab=new PrefabFile{RootObject=json};
            return prefab.Serialize().ToJsonString(new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerOptions.Default){WriteIndented=true});
        }
        finally{scene.Destroy();}
    }

    public static IReadOnlyList<Bundle> Prepare(HandTarget target,IReadOnlyList<HandBakedClip> clips,string outputModel,
        Dictionary<string,string> animations,Dictionary<string,string> assets,CancellationToken cancel)
    {
        var result=new List<Bundle>();
        foreach(var group in clips.Where(c=>c.WeaponOffset.HasValue&&c.Source.ModelPath is not null)
            .GroupBy(c=>c.Source.ModelPath!,StringComparer.OrdinalIgnoreCase))
        {
            cancel.ThrowIfCancellationRequested();
            var first=group.First();var source=first.Source;
            // Prefer the plain action over variant poses/deltas. Every selected clip still exports
            // to the hands VMDL; the graph needs exactly one sequence for each supported action.
            var actions=group.Where(c=>WeaponAnimGraph.Classify(c.Original.Name).HasValue)
                .OrderBy(c=>c.Original.Name.Length).ThenBy(c=>c.Original.Name,StringComparer.Ordinal)
                .GroupBy(c=>WeaponAnimGraph.Classify(c.Original.Name)!.Value).ToDictionary(g=>g.Key,g=>g.First());
            if(actions.Count==0)continue;
            if(!actions.ContainsKey(WeaponAction.Idle))
            {
                var idle=source.Scene.Clips.Where(c=>WeaponAnimGraph.Classify(c.Name)==WeaponAction.Idle)
                    .OrderBy(c=>c.Name.Length).ThenBy(c=>c.Name,StringComparer.Ordinal).FirstOrDefault();
                if(idle is null)throw new InvalidOperationException($"'{source.Path}' has no idle clip. Add an idle animation or turn off Auto Configure AnimGraph.");
                actions.Add(WeaponAction.Idle,HandEditorPipeline.Bake(source,idle,target,first.MotionOptions??new(){WeaponSpaceOffset=first.WeaponOffset},cancel));
            }
            var merged=actions.OrderBy(p=>p.Key).Select(p=>(Action:p.Key,Clip:WeaponClipBuilder.Combine(source.Scene.Skeleton,p.Value.Original,
                target.Skeleton,p.Value.Baked,p.Value.WeaponOffset!.Value,"hr_"+p.Key.ToString().ToLowerInvariant()))).ToArray();
            var rig=merged[0].Clip.Skeleton;
            var contents=merged.ToDictionary(p=>p.Action,p=>DmxWriter.Write(rig,p.Clip.Animation,new(){Name=p.Clip.Animation.Name,UpAxisY=false,ForwardParity=1}));
            var bind=DmxWriter.Write(rig,new Clip("bindPose",30,false,new(){Pose.Rest(rig).Locals}),new(){Name="bindPose",UpAxisY=false,ForwardParity=1});
            var graphClips=merged.Select(p=>new WeaponGraphClip(p.Action,p.Clip.Animation.Name)).ToArray();
            // Include graph schema in the content identity so future generator changes preserve old graphs.
            var identity=HandEditorPipeline.ContentKey(AnimationDriverMesh.Fbx+bind+string.Join("\n",contents.Values)+WeaponAnimGraph.Create("owner.vmdl",graphClips));
            var folder=outputModel[..^5]+"_weapons/"+HandEditorPipeline.SourceKey(source)+"/"+identity;
            var model=folder+"/animation.vmdl";var graph=folder+"/weapon.vanmgrph";
            animations[folder+"/bind.dmx"]=bind;
            assets[folder+"/driver_mesh.fbx"]=AnimationDriverMesh.Fbx;
            animations[folder+"/skeleton.dmx"]=DmxWriter.Write(rig,new Clip("bindPose",30,false,new(){Pose.Rest(rig).Locals}),new(){Name="bindPose",UpAxisY=false,ForwardParity=1,SkeletonModel=true});
            foreach(var pair in merged)animations[folder+"/"+pair.Clip.Animation.Name+".dmx"]=contents[pair.Action];
            var doc=Kv3.Parse(HandModelFactory.Create(baseModel:source.ModelPath!));
            var root=(KvObject)((KvObject)doc.Root)["rootNode"];
            root["base_model_name"]=new KvString("");
            root["anim_graph_name"]=new KvString(graph);
            var children=(KvArray)root["children"];
            var meshes=new KvArray();meshes.Items.Add(new KvObject{["_class"]=new KvString("RenderMeshFile"),["filename"]=new KvString(folder+"/driver_mesh.fbx"),["name"]=new KvString("animation_driver"),["import_scale"]=new KvDouble(1)});
            children.Items.Add(new KvObject{["_class"]=new KvString("RenderMeshList"),["children"]=meshes});
            var materials=new KvArray();materials.Items.Add(new KvObject{["_class"]=new KvString("DefaultMaterialGroup"),["use_global_default"]=new KvBool(true),["global_default_material"]=new KvString("materials/dev/reflectivity_50.vmat")});
            children.Items.Add(new KvObject{["_class"]=new KvString("MaterialGroupList"),["children"]=materials});
            children.Items.Add(new KvObject{["_class"]=new KvString("SkeletonFile"),["filename"]=new KvString(folder+"/skeleton.dmx")});
            children.Items.Add(new KvObject{["_class"]=new KvString("BoneMarkupList"),["bone_cull_type"]=new KvString("None"),["children"]=new KvArray()});
            var prepared=VmdlSetupService.Prepare(Kv3.Serialize(doc),graphClips.Select(c=>new HandAnimationEntry(c.Sequence,folder+"/"+c.Sequence+".dmx",c.Action==WeaponAction.Idle)),
                new(){ModelPath=model,BindPoseSource=folder+"/bind.dmx",AutoConfigureAnimGraph=false});
            assets[graph]=WeaponAnimGraph.Create(model,graphClips);assets[model]=prepared.VmdlText;
            var eye=HumanoidHandRetargeter.Calibration.HandViewSpace.EyePosition(source.Scene.Skeleton,source.Mapping)/2.54f;
            result.Add(new(model,graph,source.ModelPath!,target.ModelPath,graphClips,rig.Count,new global::Vector3(eye.X,eye.Y,eye.Z)));
        }
        return result;
    }
}
