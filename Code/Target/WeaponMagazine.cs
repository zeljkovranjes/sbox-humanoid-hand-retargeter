namespace HumanoidHandRetargeter.Target;

/// <summary>Magazine accounting independent of animation and input. Reload transfers ammo only on completion.</summary>
public sealed class WeaponMagazine
{
    public int Capacity { get; }
    public int Rounds { get; private set; }
    public int Reserve { get; private set; }
    public bool Reloading { get; private set; }
    public WeaponMagazine(int capacity, int reserve)
    { Capacity=Math.Max(1,capacity); Rounds=Capacity; Reserve=Math.Max(0,reserve); }
    public bool TryFire()
    { if(Reloading||Rounds==0)return false; Rounds--; return true; }
    public bool BeginReload()
    { if(Reloading||Rounds==Capacity||Reserve==0)return false; Reloading=true; return true; }
    public void FinishReload()
    { if(!Reloading)return; var count=Math.Min(Capacity-Rounds,Reserve); Rounds+=count; Reserve-=count; Reloading=false; }
    public void CancelReload()=>Reloading=false;
}
