#nullable enable
using Sandbox;
using HumanoidHandRetargeter.Target;

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
    private SceneModel? driver, weapon, hands;
    private (int From,int To)[] weaponBones=Array.Empty<(int,int)>(), handBones=Array.Empty<(int,int)>();
    private Model? loadedAnimation,loadedWeapon,loadedHands;
    private bool attack,reload,deploy,holstered,empty,skipDeploy;

    public void Attack()=>attack=true;
    public void Reload(bool emptyMagazine=false){empty=emptyMagazine;reload=true;}
    public void Deploy(bool skipAnimation=false){holstered=false;deploy=true;skipDeploy=skipAnimation;}
    public void Holster()=>holstered=true;

    protected override void OnPreRender()=>Evaluate(Time.Delta);
    private void Evaluate(float deltaTime)
    {
        if(AnimationModel!=loadedAnimation||WeaponModel!=loadedWeapon||HandsModel!=loadedHands)Release();
        if(driver is null)
        {
            if(AnimationModel is null||WeaponModel is null||HandsModel is null||AnimationModel.IsError||WeaponModel.IsError||HandsModel.IsError)return;
            (int,int)[] Map(Model model,string prefix)=>model.Bones.AllBones.Select(target=>
            {
                var name=target.Name;
                var bone=AnimationModel.Bones.GetBone(prefix+name);
                if(bone is null)throw new InvalidOperationException($"Animation model is missing '{prefix+name}'. Regenerate this weapon for the selected hands.");
                return (bone.Index,target.Index);
            }).ToArray();
            weaponBones=Map(WeaponModel,"");handBones=Map(HandsModel,WeaponClipBuilder.HandPrefix);
            loadedAnimation=AnimationModel;loadedWeapon=WeaponModel;loadedHands=HandsModel;
            driver=new SceneModel(Scene.SceneWorld,AnimationModel,global::Transform.Zero){UseAnimGraph=true,RenderingEnabled=false};
            weapon=new SceneModel(Scene.SceneWorld,WeaponModel,WorldTransform){UseAnimGraph=false};
            hands=new SceneModel(Scene.SceneWorld,HandsModel,WorldTransform){UseAnimGraph=false};
            weapon.SetAnimGraph("");hands.SetAnimGraph("");
        }
        weapon!.Transform=hands!.Transform=WorldTransform;
        driver.Transform=new global::Transform(-ViewOrigin);
        driver.SetAnimParameter("b_attack",attack);driver.SetAnimParameter("b_reload",reload);
        driver.SetAnimParameter("b_deploy",deploy);driver.SetAnimParameter("b_empty",empty);
        driver.SetAnimParameter("b_holster",holstered);driver.SetAnimParameter("b_deploy_skip",skipDeploy);
        driver.Update(deltaTime);
        attack=reload=deploy=skipDeploy=false;
        void Apply(SceneModel model,(int From,int To)[] bones)
        {
            foreach(var (from,to) in bones)model.SetBoneOverride(to,driver.GetBoneWorldTransform(from));
            model.Update(0);
        }
        Apply(weapon,weaponBones);Apply(hands,handBones);
    }
    protected override void OnDisabled()=>Release();
    protected override void OnDestroy()=>Release();
    private void Release()
    {
        driver?.Delete();weapon?.Delete();hands?.Delete();driver=weapon=hands=null;
        loadedAnimation=loadedWeapon=loadedHands=null;
    }
}
