#nullable enable
using System.Numerics;
using HumanoidHandRetargeter.Mapping;
using Skel = HumanoidHandRetargeter.Skeleton.Skeleton;

namespace HumanoidHandRetargeter.Target;
using Vector3 = System.Numerics.Vector3;

/// <summary>Conservative FPS import corrections measured in compiled model centimeters.</summary>
public sealed record HandTargetFit(float Scale, bool TurnAround)
{
    public static HandTargetFit Analyze(Skel rig, HandMappingResult mapping)
    {
        if(mapping.NeedsReview)return new(1,false);
        var arms=mapping.Hands.Where(h=>h.UpperArm.HasValue&&h.Forearm.HasValue).ToArray();
        var scale=1f;
        if(arms.Length>0)
        {
            var lengths=arms.Select(h=>Vector3.Distance(rig.RestWorld[h.UpperArm!.Value].Pos,rig.RestWorld[h.Forearm!.Value].Pos)
                +Vector3.Distance(rig.RestWorld[h.Forearm!.Value].Pos,rig.RestWorld[h.Wrist].Pos)).ToArray();
            var length=lengths.Average();
            // Leave ordinary proportions intact. Only correct gross decimal export
            // scale differences, and require agreement between both sides.
            if(float.IsFinite(length)&&length>1e-6f&&lengths.Max()/lengths.Min()<1.5f&&(length<15||length>150))
            {
                var candidate=MathF.Pow(10,MathF.Round(MathF.Log10(55/length)));
                if(length*candidate>=25&&length*candidate<=100)scale=candidate;
            }
        }
        var left=arms.FirstOrDefault(h=>h.Side==HandSide.Left);
        var right=arms.FirstOrDefault(h=>h.Side==HandSide.Right);
        var turn=false;
        if(left is not null&&right is not null)
        {
            var lateral=rig.RestWorld[left.UpperArm!.Value].Pos-rig.RestWorld[right.UpperArm!.Value].Pos;
            // s&box uses +Y on the character's left. A reversed lateral axis puts
            // each shoulder behind the opposite weapon hand. Two mirrors are a
            // proper 180-degree turn, preserving handedness and skin winding.
            turn=lateral.LengthSquared()>1e-8f&&lateral.Y/lateral.Length()<-.9f;
        }
        return new(scale,turn);
    }

    public string Apply(string vmdl)
    {
        var document=Kv3.Parse(vmdl);
        var root=(KvObject)((KvObject)document.Root).GetOrNull("rootNode")!;
        void Visit(KvObject node)
        {
            if(node.GetString("_class")=="ModelModifier_ScaleAndMirror")
            {
                // New imports use the common cm-to-inches modifier. Apply the
                // correction to meshes, bind pose and every embedded take together.
                node["scale"]=new KvDouble(Scale/2.54);
                node["mirror_x"]=new KvBool(TurnAround);
                node["mirror_y"]=new KvBool(TurnAround);
            }
            if(node.GetOrNull("children") is KvArray children)
                foreach(var child in children.Items.OfType<KvObject>())Visit(child);
        }
        Visit(root);
        return Kv3.Serialize(document);
    }
}
