#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using HumanoidHandRetargeter.Mapping;
using HumanoidHandRetargeter.Retargeting;
using HumanoidHandRetargeter.Skeleton;
using Skel = HumanoidHandRetargeter.Skeleton.Skeleton;

namespace HumanoidHandRetargeter.Editor;

/// <summary>Versioned, JSON automation boundary for editor scripts and MCP clients.</summary>
public static partial class HandRetargetApi
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy=JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive=true,
        IncludeFields=true, UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,
        Converters={new JsonStringEnumConverter()}
    };
    public sealed class Request
    {
        public int Version {get;set;}=1;
        public string Operation {get;set;}="";
        public string Path {get;set;}="";
        public float Fps {get;set;}=30;
        public string Target {get;set;}="";
        public SourceRequest[] Sources {get;set;}=Array.Empty<SourceRequest>();
        public HandMap[]? TargetMapping {get;set;}
        public Dictionary<HandSide,System.Numerics.Quaternion>? TargetPalmFrames {get;set;}
        public bool DryRun {get;set;}
        public string OutputModel {get;set;}="";
        public bool AutoConfigureAnimGraph {get;set;}=true;
        public bool Backup {get;set;}=true;
        public bool WeaponCompatible {get;set;}
        public bool PreserveSourceTracks {get;set;}=true;
        public HandMotionOptions Motion {get;set;}=new();
    }
    public sealed class SourceRequest
    {
        public string Path {get;set;}="";
        /// <summary>Exact sequence names. Empty selects all sequences.</summary>
        public string[] Clips {get;set;}=Array.Empty<string>();
        public HandMap[]? Mapping {get;set;}
        public Dictionary<HandSide,System.Numerics.Quaternion>? PalmFrames {get;set;}
    }
    public sealed class HandMap
    {
        public HandSide Side {get;set;}
        public string Wrist {get;set;}="";
        public string? UpperArm {get;set;}
        public string? Forearm {get;set;}
        public string? Clavicle {get;set;}
        public string[] Helpers {get;set;}=Array.Empty<string>();
        public DigitMap[] Digits {get;set;}=Array.Empty<DigitMap>();
    }
    public sealed class DigitMap
    {
        public DigitRole Role {get;set;}
        public string[] Segments {get;set;}=Array.Empty<string>();
        public string? Metacarpal {get;set;}
        public string? Tip {get;set;}
        public string ExtraSlot {get;set;}="";
    }
    public static string Describe()=>Serialize(new
    {
        version=1, type=typeof(HandRetargetApi).FullName,
        methods=new[]{"Describe()","Submit(string requestJson)","GetJob(string jobId)","Cancel(string jobId)","ListJobs()"},
        operations=new[]{"inspectSource","inspectTarget","convert"},
        limits=new{maxRequestCharacters=1000000,maxPendingJobs=8,maxRetainedJobs=64,maxSources=64,maxClips=256},
        defaults=new Request(),
        sourcePresets=new{human=HandEditorPipeline.HumanArms,citizen=HandEditorPipeline.CitizenArms},
        notes=new[]{"Requires a running s&box editor with this library installed.",
            "inspectTarget may create or compile an imported FBX copy in project Assets.",
            "convert writes assets transactionally and validates engine output. Cancellation during preparation can leave reusable imports.",
            "Jobs run one at a time. Poll until succeeded, failed or cancelled. Jobs are memory-only and disappear on reload.",
            "An ambiguous mapping returns mapping_required. Supply explicit bone-name mappings after inspection; no auto-confirmation.",
            "AutoConfigureAnimGraph preserves the source weapon graph through a generated live-retargeting prefab; source weapon assets remain required."}
    });
    internal static string Serialize(object value)=>JsonSerializer.Serialize(value,Json);
    internal static Request Parse(string text)
    {
        if(string.IsNullOrWhiteSpace(text)||text.Length>1000000)throw new ArgumentException("Request must contain 1 to 1000000 characters.");
        using var document=JsonDocument.Parse(text);
        if(document.RootElement.ValueKind!=JsonValueKind.Object)throw new ArgumentException("Request must be a JSON object.");
        var allowed=new HashSet<string>(new[]{"version","operation","path","fps","target","sources","targetMapping","targetPalmFrames","dryRun","outputModel","autoConfigureAnimGraph","backup","weaponCompatible","preserveSourceTracks","motion"},StringComparer.OrdinalIgnoreCase);
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var p in document.RootElement.EnumerateObject())
        {
            if(!allowed.Contains(p.Name)||!seen.Add(p.Name))throw new ArgumentException("Unknown or repeated request property: "+p.Name);
            if(p.Name.Equals("motion",StringComparison.OrdinalIgnoreCase))
                Fields(p.Value,"transferWristPosition","scaleWristTravel","wristTravelBasis","solveArmIk","preserveWeaponGrip","weaponSpaceOffset");
            if(p.Name.Equals("targetMapping",StringComparison.OrdinalIgnoreCase))MappingFields(p.Value);
            if(p.Name.Equals("sources",StringComparison.OrdinalIgnoreCase))
            {
                if(p.Value.ValueKind!=JsonValueKind.Array)throw new ArgumentException("sources must be an array.");
                foreach(var source in p.Value.EnumerateArray())
                {
                    Fields(source,"path","clips","mapping","palmFrames");
                    foreach(var field in source.EnumerateObject())if(field.Name.Equals("mapping",StringComparison.OrdinalIgnoreCase))MappingFields(field.Value);
                }
            }
        }
        var request=JsonSerializer.Deserialize<Request>(text,Json)??throw new ArgumentException("Missing request.");
        if(request.Version!=1)throw new ArgumentException("Unsupported API version. Expected 1.");
        if(!float.IsFinite(request.Fps)||request.Fps<1||request.Fps>240)throw new ArgumentException("fps must be between 1 and 240.");
        if(request.Operation is not ("inspectSource" or "inspectTarget" or "convert"))throw new ArgumentException("Unknown operation. Call Describe.");
        if(request.Operation!="convert")Required(request.Path,"path");
        else
        {
            Required(request.Target,"target");Required(request.OutputModel,"outputModel");
            Target.VmdlSetupService.NormalizeAssetPath(request.OutputModel,".vmdl");
            if(request.Sources is null||request.Sources.Length is <1 or >64)throw new ArgumentException("Supply 1 to 64 sources.");
            if(request.Motion is null)throw new ArgumentException("motion cannot be null.");
            if(request.Motion.WeaponSpaceOffset is {} offset&&(!float.IsFinite(offset.X)||!float.IsFinite(offset.Y)||!float.IsFinite(offset.Z)))
                throw new ArgumentException("weaponSpaceOffset must contain finite coordinates.");
            if(request.Motion.WristTravelBasis is {} basis)CheckQuaternion(basis);
            foreach(var source in request.Sources)
            {
                if(source is null)throw new ArgumentException("Source cannot be null.");
                Required(source.Path,"source.path");
                if(source.PalmFrames is not null)foreach(var frame in source.PalmFrames.Values)CheckQuaternion(frame);
                if(source.Clips is null||source.Clips.Any(string.IsNullOrWhiteSpace)||source.Clips.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=source.Clips.Length)
                    throw new ArgumentException("clips must contain distinct, nonempty sequence names, or [].");
            }
        }
        if(request.TargetPalmFrames is not null)foreach(var frame in request.TargetPalmFrames.Values)CheckQuaternion(frame);
        return request;
    }
    static void CheckQuaternion(System.Numerics.Quaternion value)
    {
        var length=value.LengthSquared();
        if(!float.IsFinite(length)||MathF.Abs(length-1)>.01f)throw new ArgumentException("Quaternion values must be finite and normalized.");
    }
    static void Fields(JsonElement element,params string[] allowed)
    {
        if(element.ValueKind!=JsonValueKind.Object)throw new ArgumentException("Expected an object.");
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var field in element.EnumerateObject())
            if(!allowed.Contains(field.Name,StringComparer.OrdinalIgnoreCase)||!seen.Add(field.Name))throw new ArgumentException("Unknown or repeated property: "+field.Name);
    }
    static void MappingFields(JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.Null)return;
        if(element.ValueKind!=JsonValueKind.Array)throw new ArgumentException("Mapping must be an array.");
        foreach(var hand in element.EnumerateArray())
        {
            Fields(hand,"side","wrist","upperArm","forearm","clavicle","helpers","digits");
            if(!hand.EnumerateObject().Any(p=>p.Name.Equals("side",StringComparison.OrdinalIgnoreCase)))throw new ArgumentException("Each mapping hand requires an explicit side.");
            foreach(var field in hand.EnumerateObject())if(field.Name.Equals("digits",StringComparison.OrdinalIgnoreCase))
            {
                if(field.Value.ValueKind!=JsonValueKind.Array)throw new ArgumentException("digits must be an array.");
                foreach(var digit in field.Value.EnumerateArray())
                {
                    Fields(digit,"role","segments","metacarpal","tip","extraSlot");
                    if(!digit.EnumerateObject().Any(p=>p.Name.Equals("role",StringComparison.OrdinalIgnoreCase)))throw new ArgumentException("Each digit requires an explicit role.");
                }
            }
        }
    }
    static void Required(string value,string name){if(string.IsNullOrWhiteSpace(value))throw new ArgumentException(name+" is required.");}
    static HandMappingResult Map(Skel rig,HandMap[]? overrides,HandMappingResult automatic)
    {
        if(overrides is null)return automatic;
        if(overrides.Length==0)throw new ArgumentException("Omit mapping to use detection, or supply at least one hand.");
        if(overrides.Any(h=>h is null||h.Digits is null||h.Helpers is null||h.Digits.Any(d=>d is null||d.Segments is null)))
            throw new ArgumentException("Mapping hands, digits, segments and helpers cannot be null.");
        int Bone(string name){Required(name,"bone name");var i=rig.IndexOf(name);return i>=0?i:throw new ArgumentException("Unknown bone: "+name);}
        int? Optional(string? name)=>name is null?null:Bone(name);
        return HandRigDetector.Detect(rig,overrides.Select(h=>new HandRigDefinition(h.Side,Bone(h.Wrist),
            h.Digits.Select(d=>new DigitChain(d.Role,d.Segments.Select(Bone),Optional(d.Metacarpal),Optional(d.Tip),d.ExtraSlot)),
            Optional(h.Clavicle),Optional(h.UpperArm),Optional(h.Forearm),h.Helpers.Select(Bone))));
    }
    static object Inspect(Skel rig,HandMappingResult map)=>new
    {
        boneCount=rig.Count, needsReview=map.NeedsReview, issues=map.Issues,candidates=map.Candidates,
        bones=rig.Bones.Select(b=>new{b.Index,b.Name,b.ParentIndex,restPosition=rig.RestWorld[b.Index].Pos}),
        mapping=map.Hands.Select(h=>new HandMap{Side=h.Side,Wrist=rig[h.Wrist].Name,
            UpperArm=h.UpperArm is int u?rig[u].Name:null,Forearm=h.Forearm is int f?rig[f].Name:null,Clavicle=h.Clavicle is int c?rig[c].Name:null,
            Helpers=h.TwistOrHelperBones.Select(i=>rig[i].Name).ToArray(),Digits=h.Digits.Select(d=>new DigitMap{Role=d.Role,
                Segments=d.Segments.Select(i=>rig[i].Name).ToArray(),Metacarpal=d.Metacarpal is int m?rig[m].Name:null,Tip=d.Tip is int t?rig[t].Name:null,ExtraSlot=d.ExtraSlot}).ToArray()})
    };
    static async Task<object> Run(Request request,CancellationToken token,Action<string> stage)
    {
        if(request.Operation=="inspectSource")
        {
            stage("loading source");var source=await HandEditorPipeline.LoadSourceAsync(request.Path,request.Fps,token);
            return new{source.Path,source.ModelPath,rig=Inspect(source.Scene.Skeleton,source.Mapping),source.Scene.Notes,
                clips=source.Scene.Clips.Select(c=>new{c.Name,c.Fps,c.FrameCount,c.Duration,c.Looping})};
        }
        stage("loading target");var target=await HandEditorPipeline.LoadTargetAsync(request.Operation=="inspectTarget"?request.Path:request.Target,token);
        target.Mapping=Map(target.Skeleton,request.TargetMapping,target.Mapping);
        if(request.TargetPalmFrames is not null)target.PalmFrames=request.TargetPalmFrames;
        if(request.Operation=="inspectTarget")return new{target.ModelPath,target.ImportNotes,rig=Inspect(target.Skeleton,target.Mapping)};
        if(target.Mapping.NeedsReview)throw new MappingRequiredException("target",Inspect(target.Skeleton,target.Mapping));
        var inputs=new List<(HandSource Source,Clip[] Clips)>();
        foreach(var input in request.Sources)
        {
            token.ThrowIfCancellationRequested();stage("loading source: "+input.Path);
            var source=await HandEditorPipeline.LoadSourceAsync(input.Path,request.Fps,token);
            source.Mapping=Map(source.Scene.Skeleton,input.Mapping,source.Mapping);
            if(input.PalmFrames is not null)source.PalmFrames=input.PalmFrames;
            if(source.Mapping.NeedsReview)throw new MappingRequiredException(input.Path,Inspect(source.Scene.Skeleton,source.Mapping));
            var selected=input.Clips.Length==0?source.Scene.Clips.ToArray():input.Clips.Select(name=>
                source.Scene.Clips.SingleOrDefault(c=>c.Name==name)??throw new ArgumentException("Unknown clip '"+name+"' in "+input.Path)).ToArray();
            if(selected.Length==0)throw new ArgumentException("Source contains no animation clips: "+input.Path);
            inputs.Add((source,selected));
        }
        if(inputs.Sum(i=>i.Clips.Length)>256)throw new ArgumentException("Select at most 256 clips per job.");
        var baked=new List<HandBakedClip>();
        foreach(var input in inputs)
        {
            var profile=HandEditorPipeline.Calibrate(input.Source,target);
            foreach(var clip in input.Clips)
            {
                token.ThrowIfCancellationRequested();stage("baking: "+clip.Name);
                baked.Add(await Task.Run(()=>HandEditorPipeline.Bake(input.Source,clip,target,request.Motion,token,profile),token));
            }
        }
        token.ThrowIfCancellationRequested();stage("exporting and validating");
        if(request.DryRun)return new{dryRun=true,target.ModelPath,request.OutputModel,clipCount=baked.Count,
            clips=baked.Select(b=>new{source=b.Source.Path,b.Original.Name,b.Baked.FrameCount,b.Baked.Fps,b.Notes})};
        var output=await HandEditorPipeline.ExportWithReportAsync(target,baked,request.OutputModel,request.AutoConfigureAnimGraph,
            request.Backup,request.WeaponCompatible,request.PreserveSourceTracks,token);
        return new{output,target.ImportNotes,clips=baked.Select(b=>new{source=b.Source.Path,b.Original.Name,b.Baked.FrameCount,b.Baked.Fps,b.Notes})};
    }
    sealed class MappingRequiredException : Exception
    {
        public object Details {get;}
        public MappingRequiredException(string input,object details):base("Mapping requires review: "+input){Details=details;}
    }
}
