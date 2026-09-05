#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;
using HumanoidHandRetargeter.Maths;
using HumanoidHandRetargeter.Skeleton;
using Vec=System.Numerics.Vector3;
using Quat=System.Numerics.Quaternion;

namespace HumanoidHandRetargeter.Editor;

public enum HandPreviewMode { ThirdPerson, Fps }

/// <summary>Specializes the audited PreviewWidget scene, bone overrides, overlays and orbit
/// camera. Both view modes display the same baked clip at the same playback time.</summary>
public sealed class HandPreviewWidget : SceneRenderingWidget
{
    readonly HandTarget target; readonly SceneModel model; readonly int[] modelBones; readonly Transform[] bind;
    readonly List<SceneLineObject> skeletonLines=new(),ghostLines=new(); readonly int[] framingBones;
    HandBakedClip? clip; SceneModel? weapon; HandPreviewMode mode;
    Vector3 fpsEye;Rotation fpsLook=Rotation.Identity;
    float seconds,yaw=140,pitch=20,zoom=1; Vector2 mouse;
    public bool Playing {get;set;}=true;
    public bool SkeletonOnly {get;set;}
    public bool ShowSource {get;set;}
    public bool UseAuthoredCamera {get;set;}=true;
    public float FpsFov {get;set;}=75;
    public Vector3 FpsOffset {get;set;}
    public Angles FpsAngles {get;set;}
    public HandPreviewMode Mode {get=>mode;set {mode=value;ApplyCurrentFrame();}}
    public float TimeSeconds=>seconds;
    public int FrameCount=>clip?.Baked.FrameCount??0;
    public int CurrentFrame=>clip is null?0:Math.Clamp((int)(seconds*clip.Baked.Fps),0,FrameCount-1);
    public Action<int>? FrameChanged {get;set;}
    public HandBakedClip? Clip=>clip;
    public bool HasAuthoredCamera=>clip?.Source.Scene.Skeleton.Bones.Any(b=>IsCamera(b.Name))??false;

    public HandPreviewWidget(Widget? parent,HandTarget target) : base(parent)
    {
        this.target=target;MinimumSize=new Vector2(360,360);MouseTracking=true;
        Scene=Scene.CreateEditorScene();
        using(Scene.Push()) { Camera=new GameObject(true,"camera").GetOrAddComponent<CameraComponent>(false);Camera.BackgroundColor=Theme.ControlBackground;Camera.ZNear=.05f;Camera.ZFar=4096;Camera.FieldOfView=45;Camera.Enabled=true; }
        new ScenePointLight(Scene.SceneWorld,new Vector3(120,100,120),600,Color.White*3.5f){ShadowsEnabled=false};
        new ScenePointLight(Scene.SceneWorld,new Vector3(-120,-100,90),600,Color.White*2f){ShadowsEnabled=false};
        var asset=Model.Load(target.ModelPath);model=new SceneModel(Scene.SceneWorld,asset,Transform.Zero){UseAnimGraph=false};
        modelBones=target.Skeleton.Bones.Select(b=>asset.Bones.GetBone(b.Name)?.Index??-1).ToArray();
        bind=target.Skeleton.Bones.Select(b=>asset.Bones.GetBone(b.Name)?.LocalTransform??Transform.Zero).ToArray();
        framingBones=target.Mapping.Hands.SelectMany(h=>new int?[]{h.Clavicle,h.UpperArm,h.Forearm,h.Wrist}.Where(b=>b.HasValue).Select(b=>b!.Value).Concat(h.Digits.SelectMany(d=>d.Bones))).Distinct().ToArray();
        ApplyCurrentFrame();
    }
    public void SetClip(HandBakedClip value){clip=value;seconds=0;fpsEye=FindEyePosition();fpsLook=Rotation.Identity;
        if(!HasAuthoredCamera){
            var wrists=target.Mapping.Hands.Select(h=>h.Wrist).ToArray();
            var focus=Vector3.Zero;var count=0;
            for(var frame=0;frame<value.Baked.FrameCount;frame+=Math.Max(1,value.Baked.FrameCount/60)){
                var world=new Pose(value.Baked.Frames[frame]).ToWorld(target.Skeleton);
                foreach(var wrist in wrists){focus+=HandEditorPipeline.ToEngine(world[wrist]).Position;count++;}
            }
            // Body animations have no authored view direction. Look toward the hand
            // action from the eyes without moving away to fit the arms on screen.
            if(count>0&&(focus/count-fpsEye).Length>.01f)fpsLook=Rotation.LookAt(focus/count-fpsEye,Vector3.Up);
        }
        ApplyCurrentFrame();FrameChanged?.Invoke(0);}
    Vector3 FindEyePosition()
    {
        Vector3 Rest(int bone)=>HandEditorPipeline.ToEngine(target.Skeleton.RestWorld[bone]).Position;
        var cameraBone=target.Skeleton.Bones.FirstOrDefault(b=>IsCamera(b.Name));
        if(cameraBone.Name is not null)return Rest(cameraBone.Index);
        var eyes=target.Skeleton.Bones.FirstOrDefault(b=>b.Name.Equals("eyes",StringComparison.OrdinalIgnoreCase));
        if(eyes.Name is not null)return Rest(eyes.Index);
        var head=target.Skeleton.Bones.FirstOrDefault(b=>b.Name.Equals("head",StringComparison.OrdinalIgnoreCase));
        if(head.Name is not null)return Rest(head.Index)+Vector3.Up*3;
        var arms=target.Mapping.Hands.Where(h=>h.UpperArm.HasValue).ToArray();
        if(arms.Length>0)
        {
            var shoulders=arms.Select(h=>Rest(h.UpperArm!.Value)).ToArray();
            var center=shoulders.Aggregate(Vector3.Zero,(sum,p)=>sum+p)/shoulders.Length;
            var length=arms.Average(h=>Rest(h.UpperArm!.Value).Distance(Rest(h.Wrist)));
            return center+Vector3.Up*(length*.3f)-Vector3.Forward*(length*.05f);
        }
        // Hand-only rigs have no eye/shoulder reference. Use the initial wrists,
        // not the entire animation's bounds, so a wide reload cannot pull the camera away.
        var pose=clip is null?target.Skeleton.RestWorld:new Pose(clip.Baked.Frames[0]).ToWorld(target.Skeleton);
        var wrists=target.Mapping.Hands.Select(h=>HandEditorPipeline.ToEngine(pose[h.Wrist]).Position).ToArray();
        return (wrists.Length==0?Vector3.Zero:wrists.Aggregate(Vector3.Zero,(sum,p)=>sum+p)/wrists.Length)
            -Vector3.Forward*12+Vector3.Up*4;
    }
    public void Scrub(int frame){Playing=false;if(clip is not null)seconds=Math.Clamp(frame,0,FrameCount-1)/clip.Baked.Fps;ApplyCurrentFrame();FrameChanged?.Invoke(CurrentFrame);}
    public void ResetCamera(){yaw=140;pitch=20;zoom=1;FpsOffset=Vector3.Zero;FpsAngles=default;ApplyCurrentFrame();}
    public void SetWeapon(string path)
    {
        var asset=Model.Load(path);if(asset is null||asset.IsError)throw new InvalidOperationException("Cannot load weapon model.");
        HandEditorPipeline.ValidateMaterials(asset);
        weapon?.Delete();
        weapon=new SceneModel(Scene.SceneWorld,asset,Transform.Zero){UseAnimGraph=false};ApplyCurrentFrame();
    }
    protected override void PreFrame()
    {
        Scene.EditorTick(RealTime.Now,MathF.Min(RealTime.Delta,.05f));
        if(Playing&&clip is not null&&clip.Baked.Duration>0)seconds=(seconds+MathF.Min(RealTime.Delta,.1f))%clip.Baked.Duration;
        ApplyCurrentFrame();FrameChanged?.Invoke(CurrentFrame);
    }
    static XForm[] Sample(Clip animation,float time)
    {
        var position=Math.Clamp(time*animation.Fps,0,animation.FrameCount-1);var lo=(int)position;var hi=Math.Min(lo+1,animation.FrameCount-1);var t=position-lo;
        return animation.Frames[lo].Select((v,i)=>new XForm(Vec.Lerp(v.Pos,animation.Frames[hi][i].Pos,t),Quat.Slerp(v.Rot,animation.Frames[hi][i].Rot,t))).ToArray();
    }
    public void ApplyCurrentFrame()
    {
        var pose=clip is null?Pose.Rest(target.Skeleton):new Pose(Sample(clip.Baked,seconds));
        var world=pose.ToWorld(target.Skeleton);var points=world.Select(w=>HandEditorPipeline.ToEngine(w).Position).ToArray();
        model.RenderingEnabled=!SkeletonOnly;
        for(var i=0;i<world.Length;i++)if(modelBones[i]>=0){var transform=HandEditorPipeline.ToEngine(world[i]);transform.Scale=bind[i].Scale;model.SetBoneOverride(modelBones[i],transform);}
        model.Update(0);
        Draw(skeletonLines,points,target.Skeleton.Bones.Select(b=>b.ParentIndex).ToArray(),new Color(.35f,.9f,1),SkeletonOnly);
        var indices=framingBones.Length>0?framingBones:Enumerable.Range(0,world.Length).ToArray();
        var bounds=new BBox(points[indices[0]],points[indices[0]]);foreach(var i in indices)bounds=bounds.AddPoint(points[i]);
        var restPoints=indices.Select(i=>HandEditorPipeline.ToEngine(target.Skeleton.RestWorld[i]).Position).ToArray();
        var restBounds=new BBox(restPoints[0],restPoints[0]);foreach(var point in restPoints)restBounds=restBounds.AddPoint(point);
        var radius=MathF.Max(4,restPoints.Max(p=>p.Distance(restBounds.Center)));
        Transform? camera=null; Transform? cameraRest=null;
        if(clip is not null)
        {
            var sourceWorld=new Pose(Sample(clip.Original,seconds)).ToWorld(clip.Source.Scene.Skeleton);
            Transform SourceTransform(XForm value)
            {
                if(clip.Source.Scene.UpAxis==1){var turn=Quat.CreateFromAxisAngle(Vec.UnitX,MathF.PI*.5f);value=new XForm(Vec.Transform(value.Pos,turn),Quat.Normalize(turn*value.Rot));}
                return HandEditorPipeline.ToEngine(value);
            }
            var sourcePoints=sourceWorld.Select(w=>SourceTransform(w).Position).ToArray();
            var offsets=target.Mapping.Hands.Select(h=>new {Target=h,Source=clip.Source.Mapping.Hands.FirstOrDefault(s=>s.Side==h.Side)}).Where(h=>h.Source is not null).Select(h=>points[h.Target.Wrist]-sourcePoints[h.Source!.Wrist]).ToArray();
            var contextOffset=offsets.Length==0?Vector3.Zero:offsets.Aggregate(Vector3.Zero,(sum,v)=>sum+v)/offsets.Length;
            Draw(ghostLines,sourcePoints,clip.Source.Scene.Skeleton.Bones.Select(b=>b.ParentIndex).ToArray(),new Color(1,.65f,.1f,.7f),ShowSource);
            var cameraBone=clip.Source.Scene.Skeleton.Bones.FirstOrDefault(b=>IsCamera(b.Name));
            if(HasAuthoredCamera){camera=SourceTransform(sourceWorld[cameraBone.Index]);cameraRest=SourceTransform(clip.Source.Scene.Skeleton.RestWorld[cameraBone.Index]);}
            if(weapon is not null)
            {
                foreach(var bone in weapon.Model.Bones.AllBones){var i=clip.Source.Scene.Skeleton.IndexOf(bone.Name);if(i<0)continue;var transform=SourceTransform(sourceWorld[i]);transform.Position+=contextOffset;transform.Scale=bone.LocalTransform.Scale;weapon.SetBoneOverride(bone.Index,transform);}
                weapon.Update(0);
            }
        }
        if(Mode==HandPreviewMode.Fps)
        {
            Camera.FieldOfView=Math.Clamp(FpsFov,35,120);
            var fallback=clip is null?FindEyePosition():fpsEye;
            var basis=new Transform(fallback,fpsLook);
            // Rebase authored camera motion onto the target framing. Absolute source
            // camera coordinates belong to a different rest pose and can face away from custom arms.
            if(camera is { } authored && cameraRest is { } origin && UseAuthoredCamera)
            {
                var localMotion=origin.ToLocal(authored);
                basis=basis.ToWorld(localMotion);
            }
            Camera.WorldPosition=basis.Position+basis.Rotation*FpsOffset;
            Camera.WorldRotation=basis.Rotation*Rotation.From(FpsAngles);
        }
        else
        {
            Camera.FieldOfView=45;
            var direction=Rotation.From(new Angles(pitch,yaw,0)).Forward;
            Camera.WorldPosition=bounds.Center+direction*MathX.SphereCameraDistance(radius,45)*zoom;
            Camera.WorldRotation=Rotation.LookAt(-direction,Vector3.Up);
        }
    }
    static bool IsCamera(string name)=>name.Equals("camera",StringComparison.OrdinalIgnoreCase)||name.Equals("camera_root",StringComparison.OrdinalIgnoreCase)||name.Equals("view_camera",StringComparison.OrdinalIgnoreCase);
    void Draw(List<SceneLineObject> pool,Vector3[] points,int[] parents,Color color,bool visible)
    {
        while(pool.Count<points.Length)pool.Add(new SceneLineObject(Scene.SceneWorld){Opaque=false,Lighting=false,Material=Material.Load("materials/default/default_line.vmat")});
        for(var i=0;i<pool.Count;i++){
            var line=pool[i];line.Clear();line.RenderingEnabled=visible&&i<parents.Length&&parents[i]>=0;
            if(!line.RenderingEnabled)continue;
            var a=points[parents[i]];var b=points[i];line.StartLine();line.AddLinePoint(a,color,.12f);line.AddLinePoint(b,color,.12f);line.EndLine();line.Bounds=new BBox(Vector3.Min(a,b),Vector3.Max(a,b)).Grow(4);
        }
    }
    protected override void OnMouseMove(MouseEvent e)
    {
        base.OnMouseMove(e);var delta=e.LocalPosition-mouse;mouse=e.LocalPosition;
        if((e.ButtonState&MouseButtons.Left)==0)return;
        if(Mode==HandPreviewMode.ThirdPerson){yaw-=delta.x*.4f;pitch=Math.Clamp(pitch+delta.y*.4f,-85,85);}
        else FpsAngles=new Angles(FpsAngles.pitch+delta.y*.15f,FpsAngles.yaw-delta.x*.15f,0);
    }
    protected override void OnMouseWheel(WheelEvent e)
    {
        base.OnMouseWheel(e);
        if(Mode==HandPreviewMode.ThirdPerson)Zoom(MathF.Pow(.9f,e.Delta/120f));
        else {FpsOffset+=Vector3.Forward*(e.Delta/120f);ApplyCurrentFrame();}
    }
    public void Zoom(float factor){if(Mode==HandPreviewMode.Fps)FpsOffset+=Vector3.Forward*(factor<1?2:-2);else zoom=Math.Clamp(zoom*factor,.1f,10f);ApplyCurrentFrame();}
    public byte[] RenderToPng(int size=512){ApplyCurrentFrame();var bitmap=new Bitmap(size,size);Camera.RenderToBitmap(bitmap);return bitmap.ToPng();}
    public override void OnDestroyed(){base.OnDestroyed();Scene?.Destroy();Scene=null;}
}
