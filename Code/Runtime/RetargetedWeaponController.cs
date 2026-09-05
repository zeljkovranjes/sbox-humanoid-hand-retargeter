#nullable enable
using Sandbox;
using HumanoidHandRetargeter.Target;

namespace HumanoidHandRetargeter;

/// <summary>Optional local player controls for exported weapons. Games may instead drive RetargetedWeapon directly.</summary>
[Title("Retargeted Weapon Controls"), Category("Animation")]
public sealed class RetargetedWeaponController : Component
{
    [Property] public PlayerController? Player { get; set; }
    [Property] public bool ReadInput { get; set; } = true;
    [Property] public int MagazineSize { get; set; } = 30;
    [Property] public int StartingReserve { get; set; } = 120;
    [Property] public float RoundsPerMinute { get; set; } = 800;
    [Property] public float ReloadSeconds { get; set; } = 2.7f;
    [Property] public float EmptyReloadSeconds { get; set; } = 3.2f;
    [Property] public float Damage { get; set; } = 20;
    [Property] public float Range { get; set; } = 10000;
    [Property] public float HipFov { get; set; } = 75;
    [Property] public float AimFov { get; set; } = 55;
    [Property] public string WorldGripBone { get; set; } = "hand_R";
    public int Ammo => magazine?.Rounds ?? MagazineSize;
    public int Reserve => magazine?.Reserve ?? StartingReserve;
    public bool Reloading => magazine?.Reloading ?? false;
    public bool Aiming { get; private set; }
    public int ShotsFired { get; private set; }
    public int Hits { get; private set; }
    public global::Vector3 LastHit { get; private set; }
    public Action<SceneTraceResult>? Shot { get; set; }
    private WeaponMagazine? magazine;
    private RetargetedWeapon? weapon;
    private float nextShot, reloadEnds, currentFov;
    protected override void OnStart()
    {
        weapon=GetComponent<RetargetedWeapon>();
        Player ??= Scene.GetAllComponents<PlayerController>().FirstOrDefault();
        if(Player is not null)Player.HideBodyInFirstPerson=true;
        magazine=new(MagazineSize,StartingReserve);
        currentFov=HipFov;
        weapon?.Deploy(true);
    }
    public bool Fire()
    {
        if(weapon is null||magazine is null||Player is null||Time.Now<nextShot||Reloading)return false;
        nextShot=Time.Now+60/Math.Max(1,RoundsPerMinute);
        if(!magazine.TryFire()){weapon.DryFire();return false;}
        weapon.Attack();weapon.SetEmpty(Ammo==0);ShotsFired++;
        var eye=Player.EyeTransform;
        var hit=Scene.Trace.Ray(eye.Position,eye.Position+eye.Rotation.Forward*Range)
            .IgnoreGameObjectHierarchy(Player.GameObject).IgnoreGameObjectHierarchy(GameObject).UseHitboxes().Run();
        if(hit.Hit)
        {
            Hits++;LastHit=hit.HitPosition;
            var info=new DamageInfo{Damage=Damage,Attacker=Player.GameObject,Weapon=GameObject,Position=hit.HitPosition,Origin=eye.Position};
            for(var obj=hit.GameObject;obj is not null;obj=obj.Parent)
                if(obj.GetComponent<IDamageable>() is {} damageable){damageable.OnDamage(info);break;}
        }
        Shot?.Invoke(hit);
        return true;
    }
    public bool Reload()
    {
        if(magazine is null||weapon is null||!magazine.BeginReload())return false;
        weapon.Reload(Ammo==0);reloadEnds=Time.Now+(Ammo==0?EmptyReloadSeconds:ReloadSeconds);
        return true;
    }
    public void Aim(bool value){Aiming=value&&!Reloading;if(weapon is not null)weapon.Aiming=Aiming;}
    protected override void OnUpdate()
    {
        if(weapon is null||Player is null||magazine is null)return;
        if(Reloading&&Time.Now>=reloadEnds){magazine.FinishReload();weapon.SetEmpty(Ammo==0);}
        if(ReadInput)Aim(Input.Down("Attack2"));
        if(Reloading)Aim(false);
        weapon.Aiming=Aiming;
        weapon.Sprinting=ReadInput&&Input.Down("Run")&&Player.Velocity.Length>10&&!Aiming;
        weapon.Movement=Math.Clamp(Player.Velocity.Length/150f,0,1);
        weapon.SetParameter("attack_hold",ReadInput&&Input.Down("Attack1")&&!Reloading&&Ammo>0?1f:0f);
        weapon.SetParameter("firing_mode",3);
        weapon.SetParameter("ironsights_fire_scale",.3f);
        if(ReadInput)
        {
            if(Input.Pressed("Reload"))Reload();
            if(Input.Down("Attack1")&&!weapon.Sprinting)Fire();
        }
        weapon.ShowHands=!Player.ThirdPerson;
        weapon.WorldTransform=Player.EyeTransform;
        if(Player.ThirdPerson&&Player.Renderer is {} body)
        {
            body.Set("holdtype",2);body.Set("aim_body_weight",1f);
            if(body.TryGetBoneTransform(WorldGripBone,out var hand)&&weapon.SourceBone(WorldGripBone) is {} grip)
                weapon.WorldTransform=hand.ToWorld(grip.ToLocal(global::Transform.Zero));
        }
    }
    protected override void OnPreRender()
    {
        if(Player is not null&&!Player.ThirdPerson&&Scene.Camera is {} camera)
        {
            currentFov=MathX.Lerp(currentFov,Aiming?AimFov:HipFov,Math.Clamp(Time.Delta*12,0,1));
            camera.FieldOfView=currentFov;
        }
    }
    protected override void OnDisabled(){magazine?.CancelReload();Aiming=false;if(weapon is not null)weapon.Aiming=false;}
}
