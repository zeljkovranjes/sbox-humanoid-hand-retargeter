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
    public IReadOnlyList<string> ImportNotes { get; init; } = Array.Empty<string>();
    public required string ModelPath { get; init; }
    public required Skel Skeleton { get; init; }
    public required HandMappingResult Mapping { get; set; }
    public IReadOnlyDictionary<HandSide, Quat>? PalmFrames { get; set; }
}

public sealed record HandExportResult(string ModelPath,IReadOnlyList<string> Changes,string? BackupPath,IReadOnlyList<string>? WeaponPrefabs=null);

public sealed record HandBakedClip(HandSource Source, Clip Original, Clip Baked, IReadOnlyList<string> Notes,Vec? WeaponOffset=null,HandMotionOptions? MotionOptions=null);

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
        var importNotes=new List<string>();
        if (path.EndsWith(".fbx",StringComparison.OrdinalIgnoreCase)) path = await PrepareMeshAsync(path,cancel,importNotes);
        path = ModelPath(path);
        var model = Model.Load(path);
        if (model is null || model.IsError || model.BoneCount == 0) throw new InvalidOperationException($"Cannot load a skinned model from '{path}'.");
        ValidateMaterials(model);
        var skeleton = ReadSkeleton(model);
        return new HandTarget { ImportNotes=importNotes, ModelPath=path, Skeleton=skeleton, PalmFrames=HandPresetStore.LoadPalms(skeleton), Mapping=HandPresetStore.Load(skeleton) ?? HandRigDetector.Detect(skeleton) };
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
        // Facepunch weapons author recoil as additive layers. Capture their complete firing
        // pose through the original weapon graph instead of retargeting a delta as a full pose.
        if(skeleton.IndexOf("weapon_root")>=0&&!clips.Any(c=>WeaponAnimGraph.Classify(c.Name)==WeaponAction.Fire)
            &&clips.FirstOrDefault(c=>c.Name.StartsWith("Fire_",StringComparison.OrdinalIgnoreCase)&&c.Name.EndsWith("_delta",StringComparison.OrdinalIgnoreCase)
                &&!c.Name.Contains("Hold",StringComparison.OrdinalIgnoreCase)&&!c.Name.Contains("Dry",StringComparison.OrdinalIgnoreCase)) is {} recoil)
        {
            var firing=sampling.SampleFire(Math.Max(.25f,(recoil.FrameCount-1)/recoil.Fps),fps,cancel);
            if(firing is not null)clips.Add(firing);
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
        var weaponOffset=options.WeaponSpaceOffset;
        if(source.ModelPath is not null&&options.PreserveWeaponGrip&&options.TransferWristPosition
            &&source.Scene.Skeleton.Bones.Any(b=>b.Name.Equals("weapon_root",StringComparison.OrdinalIgnoreCase)))
            weaponOffset ??= HandViewSpace.EyePosition(target.Skeleton,target.Mapping)-HandViewSpace.EyePosition(source.Scene.Skeleton,source.Mapping);
        options=new HandMotionOptions{TransferWristPosition=options.TransferWristPosition,ScaleWristTravel=options.ScaleWristTravel,
            SolveArmIk=options.SolveArmIk,PreserveWeaponGrip=options.PreserveWeaponGrip,WeaponSpaceOffset=weaponOffset,
            WristTravelBasis=options.WristTravelBasis??(source.ModelPath is not null?Quat.Identity:null)};
        var notes=weaponOffset.HasValue?profile.Notes.Concat(new[]{"Preserved both palm grip anchors in one fixed weapon space; weapon motion is not scaled per arm."}).ToArray():profile.Notes;
        return new(source,clip,HandRetargeter.Bake(profile,clip,options,cancel),notes,weaponOffset,options);
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
            var stem=SourceKey(clip.Source)+"_"+SafeName(clip.Baked.Name); var name=stem; var suffix=1;
            while(!names.Add(name)) name=stem+"_"+(++suffix);
            var content=DmxWriter.Write(exportSkeleton,ExportUnits(clip.Baked),new() {Name=name,UpAxisY=false,ForwardParity=1,ChannelExcludedBones=target.Mapping.Hands.SelectMany(h=>h.TwistOrHelperBones).ToHashSet()});
            var file=folder+"/"+name+"_"+ContentKey(content)+".dmx";
            files[file]=content;
            entries.Add(new(name,file,clip.Baked.Looping));
            if(preserveSourceTracks)
            {
                // Full source companion intentionally retains every authored camera,
                // weapon and IK track with its original hierarchy, units and timing.
                var tracks=DmxWriter.Write(clip.Source.Scene.Skeleton,clip.Original,new() {Name=name+"_source_tracks",UpAxisY=clip.Source.Scene.UpAxis==1,ForwardParity=clip.Source.ModelPath is null?2:1});
                files[folder+"/source_tracks/"+name+"_"+ContentKey(tracks)+".dmx"]=tracks;
            }
        }
        var prepared=VmdlSetupService.Prepare(original,entries,new() {ModelPath=outputModel,BindPoseSource=folder+"/bind.dmx",AutoConfigureAnimGraph=autoGraph,WeaponCompatibleArms=weaponCompatible});
        var generatedAssets=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var bundles=autoGraph?HandWeaponExport.Prepare(target,clips,outputModel,files,generatedAssets,cancel):Array.Empty<HandWeaponExport.Bundle>();
        foreach(var bundle in bundles)generatedAssets[bundle.PrefabPath]=HandWeaponExport.Prefab(bundle);
        var committed=await VmdlSetupTransaction.CommitAsync(Assets,outputModel,existing,prepared,files,async(paths,token)=>{
            await MainThread(); foreach(var path in paths) AssetSystem.RegisterFile(path);
            foreach(var bundle in bundles)
            {
                if(bundle.LiveCalibration is null)
                {
                    if(!await CompileAsync(System.IO.Path.Combine(Assets,bundle.ModelPath),token))throw new InvalidOperationException("Weapon animation model did not compile: "+bundle.ModelPath);
                    await MainThread();
                    await Task.Delay(100,token);await MainThread();
                    var owner=Model.Load(bundle.ModelPath);var graph=AnimationGraph.Load(bundle.GraphPath);
                    if(owner is null||owner.IsError||!owner.HasRenderMeshes()||owner.BoneCount<bundle.BoneCount||graph is null||graph.IsError
                        ||!bundle.Actions.All(a=>owner.AnimationNames.Contains(a.Sequence)))throw new InvalidOperationException($"Weapon animation owner validation failed: bones {owner?.BoneCount}/{bundle.BoneCount}, meshes {owner?.MeshCount}, vertices {owner?.MeshInfo.TotalVertices}, triangles {owner?.MeshInfo.TotalTriangles}, graph error {graph?.IsError}, sequences {string.Join(",",owner?.AnimationNames??Array.Empty<string>())}.");
                    if(target.Skeleton.Bones.Any(b=>owner.Bones.GetBone(WeaponClipBuilder.HandPrefix+b.Name) is null))return false;
                }
                ValidateMaterials(Model.Load(bundle.WeaponPath));
                if(!await CompileAsync(System.IO.Path.Combine(Assets,bundle.PrefabPath),token))throw new InvalidOperationException("Weapon prefab did not compile: "+bundle.PrefabPath);
                await MainThread();
                // The compiled file reaches disk before the editor's asset record updates.
                // Managed resource loading uses that record, not File.Exists.
                var prefabAsset=AssetSystem.FindByPath(bundle.PrefabPath);
                for(var wait=0;wait<50&&string.IsNullOrEmpty(prefabAsset.GetCompiledFile(true));wait++)
                {await Task.Delay(100,token);await MainThread();}
                var prefab=prefabAsset.LoadResource<PrefabFile>();
                if(prefab is null||prefab.IsError||SceneUtility.GetPrefabScene(prefab) is null)throw new InvalidOperationException("Weapon prefab could not be loaded: "+bundle.PrefabPath);
            }
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
        },backup,cancel,generatedAssets);
        return new(outputModel,prepared.Changes.Concat(bundles.Select(b=>b.LiveCalibration is not null
            ? $"Preserved complete source graph {b.GraphPath} with live custom-hand retargeting: {b.PrefabPath}"
            : $"Created weapon graph ({string.Join(", ",b.Actions.Select(a=>a.Action))}) and synchronized hands/weapon prefab: {b.PrefabPath}")).ToArray(),committed.BackupPath,bundles.Select(b=>b.PrefabPath).ToArray());
    }

    private static float ExportPositionFactor(string text)
    {
        float scale=1;
        void Walk(KvValue v) { if(v is KvObject o) { if(o.GetString("_class")=="ModelModifier_ScaleAndMirror" && o.GetOrNull("scale") is KvDouble d) scale*=(float)d.Value; foreach(var key in o.Keys) Walk(o[key]); } else if(v is KvArray a) foreach(var item in a.Items) Walk(item); }
        Walk(Kv3.Parse(text).Root);
        if(!float.IsFinite(scale)||scale<=0) throw new InvalidOperationException("Target model has an unsupported scale modifier.");
        return 1f/(2.54f*scale);
    }

    private static async Task<string> PrepareMeshAsync(string file, CancellationToken cancel,List<string> importNotes)
    {
        var bytes=File.ReadAllBytes(file);
        var materials=await Task.Run(()=>FbxMaterialAssets.Inspect(file,bytes),cancel);
        importNotes.AddRange(materials.Notes);
        var hash=materials.Signature;
        var folder="models/hand_retargeter/targets/"+SafeName(System.IO.Path.GetFileNameWithoutExtension(file))+"_"+hash+"_rig4";
        var mesh=folder+"/hands.fbx"; var model=folder+"/hands.vmdl";
        var scene=await Task.Run(()=>FbxImporter.Import(bytes,new(){SampleFps=(float)FbxScene.Build(FbxTokenizer.Parse(bytes)).FrameRate}),cancel);
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
            var entries=new List<HandAnimationEntry>();
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"bindPose"};
            for(var i=0;i<scene.Clips.Count;i++)
            {
                cancel.ThrowIfCancellationRequested();
                var clip=scene.Clips[i];
                var stem=SafeName(clip.Name).Replace('-','_');
                var name=stem;var suffix=2;
                while(!names.Add(name))name=stem+"_"+suffix++;
                var path=folder+"/embedded_"+i+".dmx";
                // Imported locals are already centimeters, matching mesh_bind and the
                // model's final unit conversion. Preserve authored takes without IK.
                File.WriteAllText(System.IO.Path.Combine(Assets,path),DmxWriter.Write(rig,clip,new(){Name=name,UpAxisY=scene.UpAxis==1}));
                AssetSystem.RegisterFile(System.IO.Path.Combine(Assets,path));
                entries.Add(new(name,path,clip.Looping));
            }
            var prepared=VmdlSetupService.Prepare(HandModelFactory.Create(mesh:mesh,meshUnitScaleCm:scene.UnitScaleCm,materialRemaps:remaps),entries,new(){ModelPath=model,BindPoseSource=bindPath,AutoConfigureAnimGraph=false});
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
    internal static string ContentKey(string text)=>Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..16];
    internal static string SourceKey(HandSource source)=>SafeName(System.IO.Path.GetFileNameWithoutExtension(source.Path))+"_"+ContentKey(source.Path.Replace('\\','/').ToLowerInvariant())[..8];

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
        public Clip? SampleFire(float duration,float fps,CancellationToken cancel)
        {
            var model=new SceneModel(world,asset,Transform.Zero){UseAnimGraph=true};
            try
            {
                if(model.AnimationGraph is null||model.AnimationGraph.IsError)return null;
                model.SetAnimParameter("skeleton",0);model.SetAnimParameter("b_deploy_skip",true);
                for(var i=0;i<120;i++){cancel.ThrowIfCancellationRequested();model.Update(1f/60);}
                model.SetAnimParameter("b_deploy_skip",false);
                var frames=new List<XForm[]>();
                for(var f=0;f<=Math.Max(1,(int)MathF.Ceiling(duration*fps));f++)
                {
                    cancel.ThrowIfCancellationRequested();model.SetAnimParameter("b_attack",f==0);model.Update(f==0?0:1f/fps);
                    var worlds=bones.Select(b=>FromEngine(model.GetBoneWorldTransform(b))).ToArray();
                    frames.Add(skeleton.Bones.Select(b=>b.ParentIndex<0?worlds[b.Index]:XForm.ToLocal(worlds[b.ParentIndex],worlds[b.Index])).ToArray());
                }
                return new Clip("Fire",fps,false,frames);
            }
            finally{model.Delete();}
        }
        public void Dispose() {world.Delete();metadataScene.Destroy();}
    }
}
