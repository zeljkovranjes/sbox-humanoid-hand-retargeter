#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HumanoidHandRetargeter.Mapping;
using Skel=HumanoidHandRetargeter.Skeleton.Skeleton;

namespace HumanoidHandRetargeter.Editor;

/// <summary>Local, skeleton-signature-keyed mapping corrections, following legacy UserPresets.</summary>
public static class HandPresetStore
{
    public sealed record Digit(DigitRole Role,int[] Segments,int? Metacarpal,int? Tip,string ExtraSlot);
    public sealed record Hand(HandSide Side,int Wrist,int? Clavicle,int? UpperArm,int? Forearm,Digit[] Digits,int[] Helpers);
    static string PathFor(Skel skeleton)
    {
        using var bytes=new MemoryStream(); using(var writer=new BinaryWriter(bytes,Encoding.UTF8,true))
            foreach(var b in skeleton.Bones) { writer.Write(b.Name);writer.Write(b.ParentIndex);writer.Write(b.RestLocal.Pos.X);writer.Write(b.RestLocal.Pos.Y);writer.Write(b.RestLocal.Pos.Z);writer.Write(b.RestLocal.Rot.X);writer.Write(b.RestLocal.Rot.Y);writer.Write(b.RestLocal.Rot.Z);writer.Write(b.RestLocal.Rot.W); }
        var signature=Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
        return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(HandEditorPipeline.Assets)!,".sbox","hand-retargeter",signature+".json");
    }
    public static HandMappingResult? Load(Skel skeleton)
    {
        var path=PathFor(skeleton); if(!File.Exists(path)) return null;
        try {
            var hands=JsonSerializer.Deserialize<Hand[]>(File.ReadAllText(path));if(hands is null)return null;
            var map=HandRigDetector.Detect(skeleton,hands.Select(h=>new HandRigDefinition(h.Side,h.Wrist,h.Digits.Select(d=>new DigitChain(d.Role,d.Segments,d.Metacarpal,d.Tip,d.ExtraSlot)),h.Clavicle,h.UpperArm,h.Forearm,h.Helpers)));
            // Older versions let users confirm every numbered ray as an extra.
            // Upgrade that placeholder only when automatic detection now identifies
            // the exact same five chains. Keep explicit anatomical corrections.
            var automatic=HandRigDetector.Detect(skeleton);
            if(!automatic.NeedsReview&&LoadPalms(skeleton) is not {Count:>0})
            {
                var upgraded=map.Hands.Select(hand=>
                {
                    var detected=automatic.Hands.FirstOrDefault(h=>h.Side==hand.Side&&h.Wrist==hand.Wrist);
                    return hand.Digits.Count==5&&hand.Digits.All(d=>d.Role==DigitRole.Extra)
                        &&detected is not null&&detected.Digits.Select(d=>d.Role).Distinct().Count()==5
                        &&!detected.Digits.Any(d=>d.Role==DigitRole.Extra)
                        &&hand.Digits.All(d=>detected.Digits.Any(a=>a.Bones.SequenceEqual(d.Bones)))?detected:hand;
                }).ToArray();
                map=HandRigDetector.Detect(skeleton,upgraded);
            }
            return map.NeedsReview?null:map;
        }
        catch(JsonException) { return null; }
        catch(ArgumentException) { return null; }
    }
    public sealed record Palm(HandSide Side,float X,float Y,float Z,float W);
    public static IReadOnlyDictionary<HandSide,System.Numerics.Quaternion>? LoadPalms(Skel skeleton)
    {
        var path=PathFor(skeleton)+".palms.json";if(!File.Exists(path))return null;
        try {
            var values=JsonSerializer.Deserialize<Palm[]>(File.ReadAllText(path));
            if(values is null)return null;
            var result=new Dictionary<HandSide,System.Numerics.Quaternion>();
            foreach(var value in values){var q=new System.Numerics.Quaternion(value.X,value.Y,value.Z,value.W);if(!float.IsFinite(q.LengthSquared())||q.LengthSquared()<1e-8f)return null;result.Add(value.Side,System.Numerics.Quaternion.Normalize(q));}
            return result;
        }catch(JsonException){return null;}catch(ArgumentException){return null;}
    }
    public static void SavePalms(Skel skeleton,IReadOnlyDictionary<HandSide,System.Numerics.Quaternion> frames)
    {
        var path=PathFor(skeleton)+".palms.json";Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path,JsonSerializer.Serialize(frames.Select(p=>new Palm(p.Key,p.Value.X,p.Value.Y,p.Value.Z,p.Value.W))));
    }
    public static void Save(Skel skeleton,HandMappingResult mapping)
    {
        if(mapping.NeedsReview) throw new InvalidOperationException("Confirm a valid mapping before saving it.");
        var path=PathFor(skeleton);Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var hands=mapping.Hands.Select(h=>new Hand(h.Side,h.Wrist,h.Clavicle,h.UpperArm,h.Forearm,h.Digits.Select(d=>new Digit(d.Role,d.Segments.ToArray(),d.Metacarpal,d.Tip,d.ExtraSlot)).ToArray(),h.TwistOrHelperBones.ToArray())).ToArray();
        File.WriteAllText(path,JsonSerializer.Serialize(hands,new JsonSerializerOptions{WriteIndented=true}));
    }
}
