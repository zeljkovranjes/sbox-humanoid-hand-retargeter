#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Editor;
using Sandbox;
using HumanoidHandRetargeter.Calibration;
using HumanoidHandRetargeter.Formats.Dmx;
using HumanoidHandRetargeter.Formats.Fbx;
using HumanoidHandRetargeter.Mapping;
using HumanoidHandRetargeter.Maths;
using HumanoidHandRetargeter.Retargeting;
using HumanoidHandRetargeter.Skeleton;
using HumanoidHandRetargeter.Target;
using Skel = HumanoidHandRetargeter.Skeleton.Skeleton;
using Vec = System.Numerics.Vector3;
using Quat = System.Numerics.Quaternion;

namespace HumanoidHandRetargeter.Editor;

public sealed class HandSource
{
    public required string Path { get; init; }
    public required SourceScene Scene { get; init; }
    public required HandMappingResult Mapping { get; set; }
    public string? ModelPath { get; init; }
    public IReadOnlyDictionary<HandSide, Quat>? PalmFrames { get; set; }
}

public sealed class HandTarget
{
    public required string ModelPath { get; init; }
    public required Skel Skeleton { get; init; }
    public required HandMappingResult Mapping { get; set; }
    public IReadOnlyDictionary<HandSide, Quat>? PalmFrames { get; set; }
}

public sealed record HandExportResult(string ModelPath,IReadOnlyList<string> Changes,string? BackupPath);

public sealed record HandBakedClip(HandSource Source, Clip Original, Clip Baked, IReadOnlyList<string> Notes);

/// <summary>Editor adapter over the pure importer, shared bake and transactional setup.
/// Engine calls use the same main-thread dispatch convention as the audited EditorPipeline.</summary>
public static class HandEditorPipeline
{
    public const string HumanArms = "models/first_person/v_first_person_arms_human.vmdl";
    public const string CitizenArms = "models/first_person/v_first_person_arms_citizen.vmdl";
    public static string Assets => Project.Current?.GetAssetsPath() ?? throw new InvalidOperationException("Open an s&box project first.");
    public static MainThreadAwaitable MainThread() => default;
    public readonly struct MainThreadAwaitable : INotifyCompletion
    {
        public MainThreadAwaitable GetAwaiter() => this;
        public bool IsCompleted => ThreadSafe.IsMainThread;
        public void OnCompleted(Action continuation) => Sandbox.MainThread.Queue(continuation);
        public void GetResult() { }
    }

    public static Transform ToEngine(XForm value)
        => new(new Vector3(value.Pos.X, value.Pos.Y, value.Pos.Z) / 2.54f, new Rotation(value.Rot.X,value.Rot.Y,value.Rot.Z,value.Rot.W));
    public static XForm FromEngine(Transform value)
        => new(new Vec(value.Position.x,value.Position.y,value.Position.z)*2.54f, new Quat(value.Rotation.x,value.Rotation.y,value.Rotation.z,value.Rotation.w));

    public static Skel ReadSkeleton(Model model)
    {
        return Skel.Create(model.Bones.AllBones.Select(b => {
            var world = FromEngine(b.LocalTransform);
            return new BoneDefinition(b.Name,b.Parent?.Name,b.Parent is null ? world : XForm.ToLocal(FromEngine(b.Parent.LocalTransform),world));
        }).ToArray());
    }

    public static async Task<HandTarget> LoadTargetAsync(string path, CancellationToken cancel = default)
    {
        await MainThread();
        if (path.EndsWith(".fbx",StringComparison.OrdinalIgnoreCase)) path = await PrepareMeshAsync(path,cancel);
        path = ModelPath(path);
        var model = Model.Load(path);
        if (model is null || model.IsError || model.BoneCount == 0) throw new InvalidOperationException($"Cannot load a skinned model from '{path}'.");
        ValidateMaterials(model);
        var skeleton = ReadSkeleton(model);
        return new HandTarget { ModelPath=path, Skeleton=skeleton, PalmFrames=HandPresetStore.LoadPalms(skeleton), Mapping=HandPresetStore.Load(skeleton) ?? HandRigDetector.Detect(skeleton) };
    }

    public static async Task<HandSource> LoadSourceAsync(string path, float fps = 30, CancellationToken cancel = default)
    {
        if (path.EndsWith(".fbx",StringComparison.OrdinalIgnoreCase))
        {
            var scene = await Task.Run(()=>FbxImporter.Import(File.ReadAllBytes(path),new FbxImportOptions {SampleFps=fps}),cancel);
            await MainThread();
            return new() { Path=path, Scene=scene, PalmFrames=HandPresetStore.LoadPalms(scene.Skeleton), Mapping=HandPresetStore.Load(scene.Skeleton) ?? HandRigDetector.Detect(scene.Skeleton) };
        }
        await MainThread();
        path = ModelPath(path);
        var model = Model.Load(path);
        if (model is null || model.IsError) throw new InvalidOperationException($"Cannot load source model '{path}'.");
        var skeleton = ReadSkeleton(model);
        var clips = new List<Clip>();
        using var sampling = new ModelSampler(model,skeleton);
        foreach (var name in model.AnimationNames.ToArray())
        {
            cancel.ThrowIfCancellationRequested();
            clips.Add(sampling.Sample(name,fps,cancel));
            await Task.Delay(1,cancel); await MainThread();
        }
        return new() { Path=path,ModelPath=path, Scene=new SourceScene(skeleton,clips,2.54f,upAxis:2,frontAxis:0,coordAxis:1),
            PalmFrames=HandPresetStore.LoadPalms(skeleton), Mapping=HandPresetStore.Load(skeleton) ?? HandRigDetector.Detect(skeleton) };
    }

    public static HandRetargetProfile Calibrate(HandSource source,HandTarget target)
        => HandRetargetProfile.Calibrate(source.Scene.Skeleton,source.Mapping,target.Skeleton,target.Mapping,source.PalmFrames,target.PalmFrames);

    public static HandBakedClip Bake(HandSource source, Clip clip, HandTarget target, HandMotionOptions options, CancellationToken cancel = default, HandRetargetProfile? profile = null)
    {
        profile ??= Calibrate(source,target);
        // Compiled s&box models already share X-forward/Z-up coordinates. A wrist's
        // bind rotation must not turn vertical reload travel into sideways/downward motion.
        if(source.ModelPath is not null && options.WristTravelBasis is null)
            options=new HandMotionOptions{TransferWristPosition=options.TransferWristPosition,ScaleWristTravel=options.ScaleWristTravel,SolveArmIk=options.SolveArmIk,WristTravelBasis=Quat.Identity};
        return new(source,clip,HandRetargeter.Bake(profile,clip,options,cancel),profile.Notes);
    }

    public static async Task<string> ExportAsync(HandTarget target,IReadOnlyList<HandBakedClip> clips,string outputModel,
        bool autoGraph,bool backup,bool weaponCompatible,bool preserveSourceTracks,CancellationToken cancel=default)
        => (await ExportWithReportAsync(target,clips,outputModel,autoGraph,backup,weaponCompatible,preserveSourceTracks,cancel)).ModelPath;

    public static async Task<HandExportResult> ExportWithReportAsync(HandTarget target, IReadOnlyList<HandBakedClip> clips, string outputModel,
        bool autoGraph, bool backup, bool weaponCompatible, bool preserveSourceTracks, CancellationToken cancel = default)
    {
        await MainThread();
        if(clips.Count==0)throw new InvalidOperationException("Select at least one animation to export.");
        if(weaponCompatible)
        {
            foreach(var clip in clips)
            {
                if(clip.Source.ModelPath is null)throw new InvalidOperationException("Weapon-compatible mode requires a weapon VMDL source so its animation owner can be verified.");
                var missing=target.Mapping.Hands.SelectMany(h=>new[]{h.Wrist}.Concat(h.Digits.SelectMany(d=>d.Bones))).Select(i=>target.Skeleton[i].Name).Where(n=>clip.Source.Scene.Skeleton.IndexOf(n)<0).ToArray();
                if(missing.Length>0)throw new InvalidOperationException("These target bones cannot bonemerge onto the selected weapon: "+string.Join(", ",missing)+". Use standalone export for this custom rig, or choose a weapon whose skeleton contains those bones.");
            }
        }
        outputModel = VmdlSetupService.NormalizeAssetPath(outputModel,".vmdl");
        var absolute = System.IO.Path.Combine(Assets,outputModel);
        var existing = File.Exists(absolute) ? File.ReadAllText(absolute) : null;
        if(existing is not null)
        {
            var existingModel=Model.Load(outputModel);
            if(existingModel is null||existingModel.IsError)throw new InvalidOperationException("The existing output model must compile before animations can be added.");
            var existingRig=ReadSkeleton(existingModel);
            if(existingRig.Count!=target.Skeleton.Count||target.Skeleton.Bones.Any(b=>existingRig.IndexOf(b.Name)<0))throw new InvalidOperationException("The existing output model has a different skeleton. Choose the target model or a new output path.");
        }
        var targetSource=System.IO.Path.Combine(Assets,target.ModelPath);
        var original = existing ?? (File.Exists(targetSource)?File.ReadAllText(targetSource):HandModelFactory.Create(baseModel:target.ModelPath));
        var folder = outputModel[..^5] + "_animations";
        var files = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<HandAnimationEntry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bindPose" };
        // A compiled target is in inches. Existing model scaling controls the animation
        // compiler's input units; new wrappers use the inherited cm-to-inch modifier.
        var factor = ExportPositionFactor(original);
        Clip ExportUnits(Clip clip) => new(clip.Name,clip.Fps,clip.Looping,clip.Frames.Select(frame=>frame.Select(t=>new XForm(t.Pos*factor,t.Rot)).ToArray()).ToList(),clip.NativeFps);
        var exportSkeleton = Skel.Create(target.Skeleton.Bones.Select(b=>new BoneDefinition(b.Name,b.ParentIndex<0?null:target.Skeleton[b.ParentIndex].Name,new XForm(b.RestLocal.Pos*factor,b.RestLocal.Rot))).ToArray());
        var bind = new Clip("bindPose",30,false,new() { Pose.Rest(exportSkeleton).Locals });
        files[folder+"/bind.dmx"] = DmxWriter.Write(exportSkeleton,bind,new() {Name="bindPose",UpAxisY=false,ForwardParity=1});
        foreach(var clip in clips)
        {
            var stem=SafeName(clip.Baked.Name); var name=stem; var suffix=1;
            while(!names.Add(name)) name=stem+"_"+(++suffix);
            var file=folder+"/"+name+".dmx";
            files[file]=DmxWriter.Write(exportSkeleton,ExportUnits(clip.Baked),new() {Name=name,UpAxisY=false,ForwardParity=1,ChannelExcludedBones=target.Mapping.Hands.SelectMany(h=>h.TwistOrHelperBones).ToHashSet()});
            entries.Add(new(name,file,clip.Baked.Looping));
            if(preserveSourceTracks)
            {
                // Full source companion intentionally retains every authored camera,
                // weapon and IK track with its original hierarchy, units and timing.
                files[folder+"/source_tracks/"+name+".dmx"]=DmxWriter.Write(clip.Source.Scene.Skeleton,clip.Original,new() {Name=name+"_source_tracks",UpAxisY=clip.Source.Scene.UpAxis==1,ForwardParity=clip.Source.ModelPath is null?2:1});
            }
        }
        var prepared=VmdlSetupService.Prepare(original,entries,new() {ModelPath=outputModel,BindPoseSource=folder+"/bind.dmx",AutoConfigureAnimGraph=autoGraph,WeaponCompatibleArms=weaponCompatible});
        var committed=await VmdlSetupTransaction.CommitAsync(Assets,outputModel,existing,prepared,files,async(paths,token)=>{
            await MainThread(); foreach(var path in paths) AssetSystem.RegisterFile(path);
            if(!await CompileAsync(absolute,token)) return false;
            await MainThread(); var model=Model.Load(outputModel);
            if(model is null || model.IsError || !prepared.Animations.All(a=>model.AnimationNames.Contains(a.SequenceName)))return false;
            if(prepared.GeneratedGraphPath is { } graphPath){var graph=AnimationGraph.Load(graphPath);if(graph is null||graph.IsError)return false;}
            ValidateMaterials(model);
            var compiledRig=ReadSkeleton(model);
            using var sampler=new ModelSampler(model,compiledRig);
            var checkedBones=target.Mapping.Hands.SelectMany(h=>new int?[]{h.Clavicle,h.UpperArm,h.Forearm,h.Wrist}.Where(i=>i.HasValue).Select(i=>i!.Value).Concat(h.Digits.SelectMany(d=>d.Bones))).Distinct().ToArray();
            for(var i=0;i<clips.Count;i++)
            {
                var expectedClip=clips[i].Baked;var actualClip=sampler.Sample(prepared.Animations[i].SequenceName,expectedClip.Fps,token);
                if(expectedClip.FrameCount>1&&actualClip.FrameCount!=expectedClip.FrameCount)return false;
                foreach(var frame in new[]{0,expectedClip.FrameCount/2,expectedClip.FrameCount-1}.Distinct())
                {
                    var expectedWorld=new Pose(expectedClip.Frames[frame]).ToWorld(target.Skeleton);
                    var actualWorld=new Pose(actualClip.Frames[frame]).ToWorld(compiledRig);
                    foreach(var bone in checkedBones)
                    {
                        var index=compiledRig.IndexOf(target.Skeleton[bone].Name);
                        if(index<0 || Vec.Distance(expectedWorld[bone].Pos,actualWorld[index].Pos)>.1f
                            || MathQ.AngleBetween(expectedWorld[bone].Rot,actualWorld[index].Rot)>.02f)
                            throw new InvalidOperationException($"Compiled sequence '{expectedClip.Name}' differs from the preview at '{target.Skeleton[bone].Name}', frame {frame}. The export was rolled back.");
                    }
                }
            }
            return true;
        },backup,cancel);
        return new(outputModel,prepared.Changes,committed.BackupPath);
    }

    private static float ExportPositionFactor(string text)
    {
        float scale=1;
        void Walk(KvValue v) { if(v is KvObject o) { if(o.GetString("_class")=="ModelModifier_ScaleAndMirror" && o.GetOrNull("scale") is KvDouble d) scale*=(float)d.Value; foreach(var key in o.Keys) Walk(o[key]); } else if(v is KvArray a) foreach(var item in a.Items) Walk(item); }
        Walk(Kv3.Parse(text).Root);
        if(!float.IsFinite(scale)||scale<=0) throw new InvalidOperationException("Target model has an unsupported scale modifier.");
        return 1f/(2.54f*scale);
    }

    private static async Task<string> PrepareMeshAsync(string file, CancellationToken cancel)
    {
        var bytes=File.ReadAllBytes(file);
        var materials=await Task.Run(()=>FbxMaterialAssets.Inspect(file,bytes),cancel);
        var hash=materials.Signature;
        var folder="models/hand_retargeter/targets/"+SafeName(System.IO.Path.GetFileNameWithoutExtension(file))+"_"+hash+"_rig3";
        var mesh=folder+"/hands.fbx"; var model=folder+"/hands.vmdl";
        var scene=await Task.Run(()=>FbxImporter.Import(bytes),cancel);
        await MainThread();
        var meshAbs=System.IO.Path.Combine(Assets,mesh); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(meshAbs)!);
        if(!File.Exists(meshAbs)) File.WriteAllBytes(meshAbs,bytes);
        var modelAbs=System.IO.Path.Combine(Assets,model);
        var remaps=HandMaterialImport.Write(materials,folder);
        if(!File.Exists(modelAbs))
        {
            // Keep the full authored hierarchy before ModelDoc can cull unweighted
            // ancestors. The audited importer preview uses this same bind-source technique.
            string EngineName(string name){var suffix=name.LastIndexOf('#');return suffix<0?name:name[..suffix]+"_duplicate";}
            var rig=Skel.Create(scene.Skeleton.Bones.Select(b=>new BoneDefinition(EngineName(b.Name),b.ParentIndex<0?null:EngineName(scene.Skeleton[b.ParentIndex].Name),b.RestLocal)).ToArray());
            var bindPath=folder+"/mesh_bind.dmx";
            File.WriteAllText(System.IO.Path.Combine(Assets,bindPath),DmxWriter.Write(rig,new Clip("bindPose",30,false,new(){Pose.Rest(rig).Locals}),new(){Name="bindPose",UpAxisY=scene.UpAxis==1}));
            var prepared=VmdlSetupService.Prepare(HandModelFactory.Create(mesh:mesh,meshUnitScaleCm:scene.UnitScaleCm,materialRemaps:remaps),Array.Empty<HandAnimationEntry>(),new(){ModelPath=model,BindPoseSource=bindPath,AutoConfigureAnimGraph=false});
            File.WriteAllText(modelAbs,prepared.VmdlText);
            AssetSystem.RegisterFile(System.IO.Path.Combine(Assets,bindPath));
        }
        AssetSystem.RegisterFile(meshAbs);
        if(!await CompileAsync(modelAbs,cancel)) throw new InvalidOperationException("The target FBX could not be compiled into a skinned model.");
        await MainThread(); return model;
    }

    public static async Task<bool> CompileAsync(string absolute, CancellationToken cancel)
    {
        await MainThread(); var asset=AssetSystem.RegisterFile(absolute);
        var compiled=absolute+"_c"; var before=File.Exists(compiled)?File.GetLastWriteTimeUtc(compiled):DateTime.MinValue;
        asset.Compile(full:true);
        var deadline=DateTime.UtcNow.AddSeconds(90);
        while(DateTime.UtcNow<deadline)
        {
            cancel.ThrowIfCancellationRequested(); await MainThread();
            if(asset.IsCompileFailed) return false;
            if(File.Exists(compiled) && File.GetLastWriteTimeUtc(compiled)>before) return true;
            await Task.Delay(100,cancel);
        }
        return false;
    }

    public static void ValidateMaterials(Model model)
    {
        var materials=model.Materials.Concat(Enumerable.Range(0,model.MaterialGroupCount).SelectMany(group=>model.GetMaterials(group))).Distinct();
        foreach(var material in materials)
        {
            if(material is null||material.IsError||material.ResourcePath.Equals("materials/error.vmat",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The model has a missing material. Import its material and texture dependencies before continuing.");
            foreach(var parameter in new[]{"g_tColor","g_tNormal","g_tRoughness","g_tMetalness","g_tAmbientOcclusion","g_tSelfIllumMask","g_tTranslucency","TextureColor","TextureNormal","TextureRoughness","TextureMetalness","TextureAmbientOcclusion","TextureSelfIllumMask","TextureTranslucency"})
                if(material.GetTexture(parameter) is {IsError:true})throw new InvalidOperationException($"Material '{material.ResourcePath}' has a missing texture ({parameter}). Restore that texture before continuing.");
            if(material.FirstTexture is {IsError:true})throw new InvalidOperationException($"Material '{material.ResourcePath}' has a missing texture.");
        }
    }

    public static string ModelPath(string path)
    {
        if(path.EndsWith(".vmdl_c",StringComparison.OrdinalIgnoreCase))path=path[..^2];
        if(System.IO.Path.IsPathRooted(path)) path=System.IO.Path.GetRelativePath(Assets,path);
        return VmdlSetupService.NormalizeAssetPath(path,".vmdl");
    }
    public static string SafeName(string name) => string.Concat(name.Select(c=>char.IsLetterOrDigit(c)||c=='_'||c=='-'?c:'_')).Trim('_') is {Length:>0} result ? result : "animation";

    private sealed class ModelSampler : IDisposable
    {
        readonly Scene metadataScene = Scene.CreateEditorScene(); readonly SkinnedModelRenderer metadata;
        readonly SceneWorld world=new(); readonly Model asset; readonly Skel skeleton; readonly int[] bones;
        public ModelSampler(Model asset, Skel rig) { using(metadataScene.Push()) { metadata=new GameObject(true,"sequence metadata").GetOrAddComponent<SkinnedModelRenderer>(); metadata.Model=asset; metadata.UseAnimGraph=false; } this.asset=asset; skeleton=rig; bones=rig.Bones.Select(b=>asset.Bones.GetBone(b.Name).Index).ToArray(); }
        public Clip Sample(string name,float fps,CancellationToken cancel=default)
        {
            var model=new SceneModel(world,asset,Transform.Zero){UseAnimGraph=false};
            try {
            model.SetAnimGraph("");model.UseAnimGraph=false;
            model.CurrentSequence.Name=name; metadata.Sequence.Name=name;
            var duration=model.CurrentSequence.Duration;
            var count=Math.Max(1,(int)MathF.Round(duration*fps)+1);
            if(count>100000) throw new InvalidOperationException($"Sequence '{name}' is too long to sample.");
            var frames=new List<XForm[]>(count);
            for(var f=0;f<count;f++) {
                cancel.ThrowIfCancellationRequested();
                model.CurrentSequence.Time=MathF.Min(f/fps,duration); model.Update(0);
                var worlds=bones.Select(b=>FromEngine(model.GetBoneWorldTransform(b))).ToArray();
                frames.Add(skeleton.Bones.Select(b=>b.ParentIndex<0?worlds[b.Index]:XForm.ToLocal(worlds[b.ParentIndex],worlds[b.Index])).ToArray());
            }
            return new(name,fps,metadata.Sequence.Looping,frames);
            } finally {model.Delete();}
        }
        public void Dispose() {world.Delete();metadataScene.Destroy();}
    }
}
