#nullable enable
using Sandbox;
using HumanoidHandRetargeter.Target;
using HumanoidHandRetargeter.Calibration;
using HumanoidHandRetargeter.Retargeting;
using HumanoidHandRetargeter.Maths;
using HumanoidHandRetargeter.Skeleton;

namespace HumanoidHandRetargeter;

/// <summary>A single graph evaluates both original weapon tracks and retargeted arm tracks.
/// Parent this component's object to the first-person camera; gameplay calls Attack/Reload/Deploy.</summary>
[Title("Retargeted FPS Weapon"), Category("Animation")]
public sealed class RetargetedWeapon : Component
{
    [Property] public Model? AnimationModel { get; set; }
    [Property] public Model? WeaponModel { get; set; }
    [Property] public Model? HandsModel { get; set; }
    [Property] public global::Vector3 ViewOrigin { get; set; }
    [Property, TextArea] public string LiveCalibration { get; set; } = "";
    [Property] public bool Visible { get; set; } = true;
    [Property] public bool ShowHands { get; set; } = true;
    [Property] public bool Aiming { get; set; }
    [Property] public bool Sprinting { get; set; }
    [Property] public float Movement { get; set; }
    [Property] public int SourceSkeleton { get; set; }
    public bool UsesOriginalGraph => !string.IsNullOrWhiteSpace(LiveCalibration);
    public AnimationGraph? OriginalGraph => driver?.AnimationGraph;
    public Action<SceneModel.AnimTagEvent>? AnimationTag { get; set; }
    private LiveHandSetup? live;
    private HandRetargetProfile? profile;
    private HandRetargeter.PoseStream? stream;
    private string? loadedCalibration;
    private readonly Dictionary<string, bool> boolParameters = new();
    private readonly Dictionary<string, float> floatParameters = new();
    private readonly Dictionary<string, int> intParameters = new();
    private readonly Dictionary<string, global::Vector3> vectorParameters = new();
    private readonly Dictionary<string, string> stringParameters = new();
    private readonly Dictionary<string, Rotation> rotationParameters = new();
    public void SetParameter(string name, bool value) => boolParameters[name] = value;
    public void SetParameter(string name, float value) => floatParameters[name] = value;
    public void SetParameter(string name, int value) => intParameters[name] = value;
    public void SetParameter(string name, global::Vector3 value) => vectorParameters[name] = value;
    public void SetParameter(string name, string value) => stringParameters[name] = value;
    public void SetParameter(string name, Rotation value) => rotationParameters[name] = value;
    public void SetEmpty(bool value) => empty = value;
    public void DryFire() => dry = true;
    public global::Transform? SourceBone(string name)
    {
        if(driver is null||WeaponModel?.Bones.GetBone(name) is not {} bone)return null;
        var transform=driver.GetBoneWorldTransform(bone.Index);
        if(UsesOriginalGraph)transform.Position-=ViewOrigin;
        return transform;
    }
    private SceneModel? driver, weapon, hands;
    private (int From,int To)[] weaponBones=Array.Empty<(int,int)>(), handBones=Array.Empty<(int,int)>();
    private Model? loadedAnimation,loadedWeapon,loadedHands;
    private bool attack,reload,deploy,holstered,empty,skipDeploy,dry;

    public void Attack()=>attack=true;
    public void Reload(bool emptyMagazine=false){empty=emptyMagazine;reload=true;}
    public void Deploy(bool skipAnimation=false){holstered=false;deploy=true;skipDeploy=skipAnimation;}
    public void Holster()=>holstered=true;

    protected override void OnPreRender()=>Evaluate(Time.Delta);
    private void Evaluate(float deltaTime)
    {
        if(AnimationModel!=loadedAnimation||WeaponModel!=loadedWeapon||HandsModel!=loadedHands||LiveCalibration!=loadedCalibration)Release();
        if(driver is null)
        {
            if(AnimationModel is null||WeaponModel is null||HandsModel is null||AnimationModel.IsError||WeaponModel.IsError||HandsModel.IsError)return;
            var owner = UsesOriginalGraph ? WeaponModel : AnimationModel;
            if (UsesOriginalGraph)
            {
                live = LiveHandSetup.Deserialize(LiveCalibration); profile = live.Calibrate();
                stream = new(profile, live.Motion);
            }
            (int,int)[] Map(Model model,string prefix)=>model.Bones.AllBones.Select(target=>
            {
                var name=target.Name;
                var bone=owner.Bones.GetBone(prefix+name);
                if(bone is null)throw new InvalidOperationException($"Animation model is missing '{prefix+name}'. Regenerate this weapon for the selected hands.");
                return (bone.Index,target.Index);
            }).ToArray();
            weaponBones=Map(WeaponModel,"");
            handBones=UsesOriginalGraph ? profile!.Target.Bones.Select(b => (b.Index, HandsModel.Bones.GetBone(b.Name)?.Index
                ?? throw new InvalidOperationException("Target model changed; re-export calibration for " + b.Name))).ToArray() : Map(HandsModel,WeaponClipBuilder.HandPrefix);
            loadedAnimation=AnimationModel;loadedWeapon=WeaponModel;loadedHands=HandsModel;
            loadedCalibration=LiveCalibration;
            driver=new SceneModel(Scene.SceneWorld,owner,global::Transform.Zero){UseAnimGraph=true,RenderingEnabled=false};
            driver.OnAnimTagEvent=tag=>AnimationTag?.Invoke(tag);
            if (UsesOriginalGraph && (driver.AnimationGraph is null || driver.AnimationGraph.IsError))
                throw new InvalidOperationException("Source weapon graph is unavailable. Restore its dependencies before using this prefab.");
            weapon=new SceneModel(Scene.SceneWorld,WeaponModel,WorldTransform){UseAnimGraph=false};
            hands=new SceneModel(Scene.SceneWorld,HandsModel,WorldTransform){UseAnimGraph=false};
            weapon.SetAnimGraph("");hands.SetAnimGraph("");
        }
        weapon!.Transform=hands!.Transform=WorldTransform;
        weapon.RenderingEnabled=Visible;hands.RenderingEnabled=Visible&&ShowHands;
        driver.Transform=UsesOriginalGraph ? global::Transform.Zero : new global::Transform(-ViewOrigin);
        driver.SetAnimParameter("b_attack",attack);driver.SetAnimParameter("b_reload",reload);
        driver.SetAnimParameter("b_deploy",deploy);driver.SetAnimParameter("b_empty",empty);
        driver.SetAnimParameter("b_holster",holstered);driver.SetAnimParameter("b_deploy_skip",skipDeploy);
        if (UsesOriginalGraph)
        {
            driver.SetAnimParameter("ironsights", Aiming ? 1 : 0);
            driver.SetAnimParameter("skeleton", SourceSkeleton);
            driver.SetAnimParameter("b_sprint", Sprinting);
            driver.SetAnimParameter("move_bob", Movement);
            driver.SetAnimParameter("b_attack_dry", dry);
        }
        foreach(var p in boolParameters)driver.SetAnimParameter(p.Key,p.Value);
        foreach(var p in intParameters)driver.SetAnimParameter(p.Key,p.Value);
        foreach(var p in floatParameters)driver.SetAnimParameter(p.Key,p.Value);
        foreach(var p in vectorParameters)driver.SetAnimParameter(p.Key,p.Value);
        foreach(var p in stringParameters)driver.SetAnimParameter(p.Key,p.Value);
        foreach(var p in rotationParameters)driver.SetAnimParameter(p.Key,p.Value);
        driver.Update(deltaTime);
        attack=reload=deploy=skipDeploy=dry=false;
        if (UsesOriginalGraph)
        {
            var world = profile!.Source.Bones.Select(b => FromEngine(driver.GetBoneWorldTransform(ownerIndex(b.Name)))).ToArray();
            int ownerIndex(string name) => WeaponModel!.Bones.GetBone(name).Index;
            var locals = profile.Source.Bones.Select(b => b.ParentIndex < 0 ? world[b.Index] : XForm.ToLocal(world[b.ParentIndex],world[b.Index])).ToArray();
            var solved = stream!.Step(new Pose(locals)).ToWorld(profile.Target);
            foreach(var (from,to) in weaponBones)
            {
                var transform=driver.GetBoneWorldTransform(from);transform.Position-=ViewOrigin;
                weapon.SetBoneOverride(to,transform);
            }
            foreach(var (from,to) in handBones)
            {
                var pose=solved[from];pose.Pos-=live!.Motion.WeaponSpaceOffset ?? System.Numerics.Vector3.Zero;
                var transform=ToEngine(pose);transform.Position-=ViewOrigin;hands.SetBoneOverride(to,transform);
            }
            weapon.Update(0);hands.Update(0);
            return;
        }
        void Apply(SceneModel model,(int From,int To)[] bones)
        {
            foreach(var (from,to) in bones)model.SetBoneOverride(to,driver.GetBoneWorldTransform(from));
            model.Update(0);
        }
        Apply(weapon,weaponBones);Apply(hands,handBones);
    }
    static XForm FromEngine(global::Transform t) => new(new System.Numerics.Vector3(t.Position.x,t.Position.y,t.Position.z)*2.54f,
        new System.Numerics.Quaternion(t.Rotation.x,t.Rotation.y,t.Rotation.z,t.Rotation.w));
    static global::Transform ToEngine(XForm t) => new(new global::Vector3(t.Pos.X,t.Pos.Y,t.Pos.Z)/2.54f,
        new Rotation(t.Rot.X,t.Rot.Y,t.Rot.Z,t.Rot.W));
    protected override void OnDisabled()=>Release();
    protected override void OnDestroy()=>Release();
    private void Release()
    {
        driver?.Delete();weapon?.Delete();hands?.Delete();driver=weapon=hands=null;
        loadedAnimation=loadedWeapon=loadedHands=null;
        live=null;profile=null;stream=null;loadedCalibration=null;
    }
}
