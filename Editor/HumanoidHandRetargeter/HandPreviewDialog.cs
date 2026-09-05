#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;

namespace HumanoidHandRetargeter.Editor;

/// <summary>Legacy PreviewDialog layout, with Third Person/FPS camera controls.</summary>
public sealed class HandPreviewDialog : Dialog
{
    public HandPreviewWidget Preview {get;}
    public Action? Confirmed {get;set;}
    public HandPreviewDialog(Widget? parent,HandTarget target,IReadOnlyList<HandBakedClip> clips) : base(parent)
    {
        Window.WindowTitle="Preview - "+System.IO.Path.GetFileName(target.ModelPath);Window.SetWindowIcon("preview");Window.SetModal(true,true);
        Window.MinimumWidth=560;Window.MinimumHeight=560;Window.Size=new Vector2(760,780);
        Layout=Layout.Column();Layout.Margin=12;Layout.Spacing=8;
        Preview=Layout.Add(new HandPreviewWidget(this,target),1);
        var error=Layout.Add(new Label(this){WordWrap=true,Visible=false});error.SetStyles($"color:{Theme.Red.Hex};");
        var modeRow=Layout.AddRow();modeRow.Spacing=8;
        var modes=modeRow.Add(new ComboBox(this){MinimumWidth=150});
        modes.AddItem("Third Person","3d_rotation",()=>Preview.Mode=HandPreviewMode.ThirdPerson,selected:true);
        modes.AddItem("FPS","first_page",()=>Preview.Mode=HandPreviewMode.Fps);
        modeRow.Add(new Button("Reset camera","center_focus_strong"){Clicked=Preview.ResetCamera});
        modeRow.Add(new Button("−"){Clicked=()=>Preview.Zoom(1.2f)});modeRow.Add(new Button("+"){Clicked=()=>Preview.Zoom(.8f)});
        modeRow.Add(new Button("Weapon model…","view_in_ar"){Clicked=()=>{var picker=AssetPicker.Create(this,AssetType.Model);picker.OnAssetPicked=assets=>{var a=assets.FirstOrDefault();if(a is not null){try{Preview.SetWeapon(a.Path);error.Visible=false;}catch(Exception ex){error.Text=ex.Message;error.Visible=true;}}};picker.Show();}});
        var cameraRow=Layout.AddRow();cameraRow.Spacing=6;
        var authored=cameraRow.Add(new Checkbox("Authored camera motion"){Value=true});authored.Clicked=()=>Preview.UseAuthoredCamera=authored.Value;
        cameraRow.Add(new Label(this){Text="FOV:"});var fov=cameraRow.Add(new LineEdit(this){Text="75",FixedWidth=42});
        cameraRow.Add(new Label(this){Text="FPS offset X / Y / Z:"});var offsets=Enumerable.Range(0,3).Select(_=>cameraRow.Add(new LineEdit(this){Text="0",FixedWidth=42})).ToArray();
        cameraRow.Add(new Button("Apply"){Clicked=()=>{if(float.TryParse(fov.Text,out var f)&&float.IsFinite(f))Preview.FpsFov=f;var values=offsets.Select(e=>float.TryParse(e.Text,out var v)&&float.IsFinite(v)?v:0).ToArray();Preview.FpsOffset=new Vector3(values[0],values[1],values[2]);Preview.ApplyCurrentFrame();}});
        var clipRow=Layout.AddRow();clipRow.Spacing=8;clipRow.Add(new Label(this){Text="Clip:"});var combo=clipRow.Add(new ComboBox(this){MinimumWidth=220},1);
        for(var i=0;i<clips.Count;i++){var clip=clips[i];combo.AddItem(clip.Baked.Name,"movie",()=>Preview.SetClip(clip),selected:i==0);}
        var transport=Layout.AddRow();transport.Spacing=8;
        var play=transport.Add(new Button("","pause"){FixedWidth=28,FixedHeight=24});play.Clicked=()=>{Preview.Playing=!Preview.Playing;play.Icon=Preview.Playing?"pause":"play_arrow";};
        var slider=transport.Add(new FloatSlider(this),1);slider.Minimum=0;slider.OnValueEdited=()=>Preview.Scrub((int)slider.Value);
        var label=transport.Add(new Label(this){Text="0 / 0",FixedWidth=80});
        var ghost=transport.Add(new Button("Show source","compare"){IsToggle=true,FixedHeight=24});ghost.Clicked=()=>Preview.ShowSource=ghost.IsChecked;
        var skeleton=transport.Add(new Button("Skeleton","polyline"){IsToggle=true,FixedHeight=24});skeleton.Clicked=()=>Preview.SkeletonOnly=skeleton.IsChecked;
        Preview.FrameChanged=frame=>{if(modes.CurrentIndex!=(int)Preview.Mode)modes.CurrentIndex=(int)Preview.Mode;play.Icon=Preview.Playing?"pause":"play_arrow";slider.Maximum=Math.Max(0,Preview.FrameCount-1);slider.Value=frame;label.Text=$"{frame+1} / {Preview.FrameCount}";authored.Enabled=Preview.HasAuthoredCamera;};
        var footer=Layout.AddRow();footer.Spacing=8;footer.AddStretchCell();footer.Add(new Button("Cancel"){Clicked=Close});
        footer.Add(new Button.Primary("Looks good - Convert"){Icon="check",Tint=Theme.Green,Clicked=()=>{Confirmed?.Invoke();Close();}});
        if(clips.Count>0)Preview.SetClip(clips[0]);
    }
}
