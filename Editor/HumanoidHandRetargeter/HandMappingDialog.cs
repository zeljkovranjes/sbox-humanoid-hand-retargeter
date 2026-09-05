#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;
using HumanoidHandRetargeter.Mapping;
using HumanoidHandRetargeter.Calibration;
using Quat=System.Numerics.Quaternion;
using Vec=System.Numerics.Vector3;
using Skel=HumanoidHandRetargeter.Skeleton.Skeleton;

namespace HumanoidHandRetargeter.Editor;

/// <summary>Legacy MappingEditor layout with variable hand/digit roles and optional arms.</summary>
public sealed class HandMappingDialog : Dialog
{
    readonly Skel skeleton; readonly List<HandEdit> hands=new(); readonly Label error;
    public IReadOnlyDictionary<HandSide,Quat> PalmFrames {get;private set;}=new Dictionary<HandSide,Quat>();
    public Action<HandMappingResult>? Applied {get;set;}
    public HandMappingDialog(Widget? parent,string name,Skel rig,HandMappingResult mapping,IReadOnlyDictionary<HandSide,Quat>? palms=null) : base(parent)
    {
        skeleton=rig;Window.WindowTitle="Hand Mapping - "+name;Window.SetWindowIcon("device_hub");Window.SetModal(true,true);
        Window.MinimumWidth=460;Window.MinimumHeight=600;Window.Size=new Vector2(520,720);
        Layout=Layout.Column();Layout.Margin=12;Layout.Spacing=8;
        Layout.Add(new Label(this){Text="Confirm each hand and its ordered finger joints. Leave missing roles empty. Metacarpals and terminal tips are separate from bending joints.",WordWrap=true});
        var scroll=Layout.Add(new ScrollArea(this),1);scroll.Canvas=new Widget(scroll);scroll.Canvas.Layout=Layout.Column();scroll.Canvas.Layout.Margin=new Sandbox.UI.Margin(4,4,16,4);scroll.Canvas.Layout.Spacing=4;
        foreach(var side in Enum.GetValues<HandSide>())
        {
            var current=mapping.Hands.FirstOrDefault(h=>h.Side==side);var edit=new HandEdit(side,current);hands.Add(edit);
            var header=scroll.Canvas.Layout.Add(new Checkbox(side+" hand"){Value=current is not null});header.Clicked=()=>edit.Enabled=header.Value;
            var canvas=scroll.Canvas.Layout;
            Choice(canvas,"Wrist",edit.Wrist,v=>edit.Wrist=v);
            var manual=canvas.Add(new Checkbox("Manual palm directions (partial rigs)"){Value=palms?.ContainsKey(side)??false});
            edit.ManualPalm=manual;
            var frame=palms is not null && palms.TryGetValue(side,out var saved)?saved:Quat.Identity;
            LineEdit[] Direction(string title,Vec vector){var row=canvas.AddRow();row.Spacing=4;row.Add(new Label(this){Text=title,FixedWidth=130});return new[]{vector.X,vector.Y,vector.Z}.Select(v=>row.Add(new LineEdit(this){Text=v.ToString("G6"),MinimumWidth=40},1)).ToArray();}
            edit.Forward=Direction("Forward X / Y / Z",Vec.Transform(Vec.UnitX,frame));
            edit.Dorsal=Direction("Dorsal X / Y / Z",Vec.Transform(Vec.UnitZ,frame));
            manual.ToolTip="Model-space directions: forward points from wrist to fingers; dorsal points out of the back of the hand. Use when a palm plane cannot be detected.";
            Choice(canvas,"Clavicle",edit.Clavicle,v=>edit.Clavicle=v);
            Choice(canvas,"Upper arm",edit.Upper,v=>edit.Upper=v);
            Choice(canvas,"Forearm",edit.Fore,v=>edit.Fore=v);
            foreach(var role in new[]{DigitRole.Thumb,DigitRole.Index,DigitRole.Middle,DigitRole.Ring,DigitRole.Pinky})
                DigitRows(canvas,edit,new DigitEdit(role,current?.Digits.FirstOrDefault(d=>d.Role==role)));
            foreach(var digit in current?.Digits.Where(d=>d.Role==DigitRole.Extra)??Enumerable.Empty<DigitChain>())
                DigitRows(canvas,edit,new DigitEdit(DigitRole.Extra,digit));
            var add=canvas.Add(new Button("Add extra digit","add"));
            add.Clicked=()=>DigitRows(canvas,edit,new DigitEdit(DigitRole.Extra,null){ExtraSlot="extra"+edit.Digits.Count(d=>d.Role==DigitRole.Extra)});
        }
        error=Layout.Add(new Label(this){WordWrap=true});error.SetStyles($"color: {Theme.Red.Hex};");
        var footer=Layout.AddRow();footer.Spacing=8;
        var save=footer.Add(new Checkbox("Save mapping for this rig"){Value=true});footer.AddStretchCell();
        footer.Add(new Button("Cancel"){Clicked=Close});
        footer.Add(new Button.Primary("Apply Mapping"){Icon="check",Clicked=()=>{
            try{
                var rigs=hands.Where(h=>h.Enabled).Select(h=>{
                    if(h.Wrist is null) throw new InvalidOperationException(h.Side+" wrist is required.");
                    var digits=h.Digits.Where(d=>d.Segments.Any(v=>v.HasValue)).Select(d=>new DigitChain(d.Role,d.Segments.Where(v=>v.HasValue).Select(v=>v!.Value),d.Meta,d.Tip,d.ExtraSlot)).ToArray();
                    return new HandRigDefinition(h.Side,h.Wrist.Value,digits,h.Clavicle,h.Upper,h.Fore,h.Helpers);
                }).ToArray();
                var result=HandRigDetector.Detect(skeleton,rigs);
                if(result.NeedsReview) throw new InvalidOperationException(string.Join("\n",result.Issues.Select(i=>i.Message)));
                var frames=new Dictionary<HandSide,Quat>();
                foreach(var hand in hands.Where(h=>h.Enabled&&h.ManualPalm.Value)) {
                    Vec Read(LineEdit[] fields){var v=fields.Select(f=>float.TryParse(f.Text,out var n)&&float.IsFinite(n)?n:throw new InvalidOperationException("Palm directions must be finite numbers.")).ToArray();return new Vec(v[0],v[1],v[2]);}
                    if(!HandRetargetProfile.TryCreatePalmFrame(Read(hand.Forward),Read(hand.Dorsal),out var frame))throw new InvalidOperationException("Palm forward and dorsal directions must be nonzero and not parallel.");
                    frames.Add(hand.Side,frame);
                }
                PalmFrames=frames;
                if(save.Value){HandPresetStore.Save(skeleton,result);HandPresetStore.SavePalms(skeleton,frames);}Applied?.Invoke(result);Close();
            }catch(Exception ex){error.Text=ex.Message;}
        }});
    }
    void Choice(Layout layout,string label,int? value,Action<int?> changed)
    {
        var row=layout.AddRow();row.Spacing=8;row.Add(new Label(this){Text=label,FixedWidth=130});
        var combo=row.Add(new ComboBox(this),1);combo.AddItem("<none>","block",()=>changed(null),selected:value is null);
        foreach(var bone in skeleton.Bones){var index=bone.Index;combo.AddItem(bone.Name,null,()=>changed(index),selected:value==index);}
    }
    void DigitRows(Layout canvas,HandEdit hand,DigitEdit digit)
    {
        hand.Digits.Add(digit);
        var title=canvas.Add(new Label(this){Text=hand.Side+" "+digit.Role+(digit.Role==DigitRole.Extra?" "+digit.ExtraSlot:"")});title.SetStyles($"font-weight:600;color:{Theme.Blue.Hex};margin-top:8px;");
        Choice(canvas,"Metacarpal",digit.Meta,v=>digit.Meta=v);
        var jointRows=canvas.AddColumn();
        void AddJoint(int? value){var index=digit.Segments.Count;digit.Segments.Add(value);Choice(jointRows,"Joint "+(index+1),value,v=>digit.Segments[index]=v);}
        var initial=digit.Segments.ToArray();digit.Segments.Clear();foreach(var v in initial)AddJoint(v);
        var add=canvas.Add(new Button("Add joint","add"));add.Clicked=()=>AddJoint(null);
        Choice(canvas,"Terminal tip",digit.Tip,v=>digit.Tip=v);
    }
    sealed class HandEdit
    {
        public Checkbox ManualPalm=null!; public LineEdit[] Forward=Array.Empty<LineEdit>(),Dorsal=Array.Empty<LineEdit>();
        public HandSide Side;public bool Enabled;public int? Wrist,Clavicle,Upper,Fore;public int[] Helpers;public List<DigitEdit> Digits=new();
        public HandEdit(HandSide side,HandRigDefinition? h){Side=side;Enabled=h is not null;Wrist=h?.Wrist;Clavicle=h?.Clavicle;Upper=h?.UpperArm;Fore=h?.Forearm;Helpers=h?.TwistOrHelperBones.ToArray()??Array.Empty<int>();}
    }
    sealed class DigitEdit
    {
        public DigitRole Role;public int? Meta,Tip;public string ExtraSlot;public List<int?> Segments;
        public DigitEdit(DigitRole role,DigitChain? d){Role=role;Meta=d?.Metacarpal;Tip=d?.Tip;ExtraSlot=d?.ExtraSlot??"";Segments=d?.Segments.Select(i=>(int?)i).ToList()??new(){null,null,null};}
    }
}
