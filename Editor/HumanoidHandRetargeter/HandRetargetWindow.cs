#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Editor;
using Sandbox;
using HumanoidHandRetargeter.Retargeting;
using HumanoidHandRetargeter.Skeleton;

namespace HumanoidHandRetargeter.Editor;

/// <summary>The audited RetargetWindow shell specialized for hands, with the same toolbar,
/// scroll rows, three option columns, report area and status strip.</summary>
[Dock("Editor",DockTitle,"sync_alt")]
public sealed class HandRetargetWindow : Widget
{
    public const string DockTitle="Humanoid Hand Retargeter";
    static HandRetargetWindow? instance;
    readonly List<Row> rows=new();readonly Layout list;readonly Label status;readonly Label report;
    readonly Button convert,add,details;readonly ComboBox targets;readonly Widget options;
    readonly Checkbox graph,backup,ik,travel,scale,companion,weapon,grip;
    readonly LineEdit output,fps;bool busy;CancellationTokenSource? cancellation;
    public HandTarget? Target {get;private set;}
    public IReadOnlyList<HandBakedClip> LastBake {get;private set;}=Array.Empty<HandBakedClip>();
    public bool AutoConfigureAnimGraph {get=>graph.Value;set=>graph.Value=value;}
    public string OutputPath {get=>output.Text;set=>output.Text=value;}

    public HandRetargetWindow(Widget? parent) : base(parent)
    {
        instance=this;Name="HumanoidHandRetargeter";WindowTitle=DockTitle;SetWindowIcon("sync_alt");MinimumSize=new Vector2(720,420);
        Layout=Layout.Column();var top=Layout.AddRow();top.Margin=8;top.Spacing=8;
        add=top.Add(new Button.Primary("Add Files…"){Icon="add",Clicked=PickSources});
        top.AddSpacingCell(8);top.Add(new Label(this){Text="Target:"});targets=top.Add(new ComboBox(this){MinimumWidth=190});
        targets.AddItem("Human FPS arms","person",()=>_=SelectTargetAsync(HandEditorPipeline.HumanArms),selected:true);
        targets.AddItem("Citizen FPS arms","person_outline",()=>_=SelectTargetAsync(HandEditorPipeline.CitizenArms));
        targets.AddItem("Custom model (.vmdl)…","view_in_ar",PickTargetModel);
        targets.AddItem("Custom hands (.fbx)…","category",PickTargetFbx);
        top.AddSpacingCell(8);top.Add(new Label(this){Text="Output:"});var outputMode=top.Add(new ComboBox(this){MinimumWidth=200});
        outputMode.AddItem("New animation vmdl","note_add",()=>{if(output is not null)output.Text="models/hand_retargeter/retargeted_hands.vmdl";},selected:true);
        outputMode.AddItem("Add to existing vmdl…","library_add",()=>{
            var picker=AssetPicker.Create(this,AssetType.Model);picker.OnAssetPicked=assets=>{var asset=assets.FirstOrDefault();if(asset is not null && output is not null)output.Text=asset.Path;};picker.Show();
        });
        top.AddStretchCell();convert=top.Add(new Button.Primary("Convert All"){Icon="play_arrow",Tint=Theme.Green,Clicked=()=>_=ConvertAsync()});
        var scroll=Layout.Add(new ScrollArea(this),1);scroll.Canvas=new Widget(scroll);scroll.Canvas.Layout=Layout.Column();scroll.Canvas.Layout.Margin=new Sandbox.UI.Margin(8,4,16,4);scroll.Canvas.Layout.Spacing=2;list=scroll.Canvas.Layout;
        options=Layout.Add(new Group(this){Title="Options",Icon="tune"});options.Layout=Layout.Row();options.Layout.Margin=new Sandbox.UI.Margin(14,30,14,12);options.Layout.Spacing=24;
        var a=options.Layout.AddColumn();a.Spacing=6;
        graph=a.Add(new Checkbox("Auto Configure AnimGraph"){Value=true});graph.ToolTip="Generate a weapon action graph and synchronized hands/weapon prefab for weapon clips. Other clips get an idle/bind graph when missing. Existing graphs are preserved.";
        backup=a.Add(new Checkbox("Back up target VMDL"){Value=true});
        weapon=a.Add(new Checkbox("Weapon-compatible arms"){Value=false});weapon.ToolTip="Keep animation graph ownership on the weapon. Arms are bonemerged onto it.";
        companion=a.Add(new Checkbox("Preserve source tracks"){Value=true});companion.ToolTip="Also export the original hierarchy and animation as a companion DMX, preserving weapon, camera and IK tracks.";
        var b=options.Layout.AddColumn();b.Spacing=6;
        ik=b.Add(new Checkbox("Arm effector IK"){Value=true});travel=b.Add(new Checkbox("Transfer wrist travel"){Value=true});scale=b.Add(new Checkbox("Scale wrist travel to target"){Value=true});
        grip=b.Add(new Checkbox("Preserve weapon grip"){Value=true});grip.ToolTip="For weapon model sources, keep both palm anchors on the authored weapon motion. Per-arm travel scaling is bypassed.";
        b.Add(new Button("Target Mapping…","device_hub"){Clicked=MapTarget});
        var select=b.AddRow();select.Add(new Button("Select all"){Clicked=()=>{foreach(var row in rows)row.Selected=true;Refresh();}});select.Add(new Button("Select none"){Clicked=()=>{foreach(var row in rows)row.Selected=false;Refresh();}});
        var c=options.Layout.AddColumn();c.Spacing=6;
        c.Add(new Label(this){Text="Output model:"});output=c.Add(new LineEdit(this){Text="models/hand_retargeter/retargeted_hands.vmdl",MinimumWidth=200});
        var sample=c.AddRow();sample.Spacing=8;sample.Add(new Label(this){Text="Sample fps:"});fps=sample.Add(new LineEdit(this){Text="30",FixedWidth=52});
        foreach(var check in new[]{ik,travel,scale,grip})check.Clicked=()=>LastBake=Array.Empty<HandBakedClip>();
        report=Layout.Add(new Label(this){WordWrap=true,Visible=false});report.SetStyles("margin: 8px;");
        var bottom=Layout.AddRow();bottom.Margin=new Sandbox.UI.Margin(8,4,8,6);bottom.Spacing=8;
        status=bottom.Add(new Label(this){Text="Select source animations and a target hand model."},1);
        details=bottom.Add(new Button("Show details"){Visible=false,Clicked=ToggleDetails});
        bottom.Add(new Button("Cancel"){Clicked=()=>cancellation?.Cancel()});
        _=SelectTargetAsync(HandEditorPipeline.HumanArms);
    }
    public static HandRetargetWindow? Open()
    {
        if(instance is null||!instance.IsValid)EditorWindow.DockManager.SetDockState(DockTitle,true);
        if(instance is { IsValid: true })EditorWindow.DockManager.RaiseDock(instance);return instance;
    }
    void PickSources(){var picker=new FileDialog(null){Title="Add hand animations"};picker.SetFindExistingFiles();picker.SetModeOpen();picker.SetNameFilter("Hand animation files (*.fbx *.vmdl *.vmdl_c)");if(picker.Execute())_=AddFilesAsync(picker.SelectedFiles.ToArray());}
    void PickTargetModel(){var picker=AssetPicker.Create(this,AssetType.Model);picker.OnAssetPicked=assets=>{var asset=assets.FirstOrDefault();if(asset is not null)_=SelectTargetAsync(asset.Path);};picker.Show();}
    void PickTargetFbx(){var picker=new FileDialog(null){Title="Select target FPS hands"};picker.SetFindExistingFiles();picker.SetModeOpen();picker.SetNameFilter("Hand models (*.fbx)");if(picker.Execute()&&picker.SelectedFiles.FirstOrDefault() is {} path)_=SelectTargetAsync(path);}

    public Task SelectTargetAsync(string path)=>Run(async token=>{
        Target=await HandEditorPipeline.LoadTargetAsync(path,token);
        targets.CurrentIndex=path.Equals(HandEditorPipeline.HumanArms,StringComparison.OrdinalIgnoreCase)?0
            :path.Equals(HandEditorPipeline.CitizenArms,StringComparison.OrdinalIgnoreCase)?1
            :path.EndsWith(".fbx",StringComparison.OrdinalIgnoreCase)?3:2;
        LastBake=Array.Empty<HandBakedClip>();
        status.Text="Target: "+System.IO.Path.GetFileName(path)+(Target.Mapping.NeedsReview?" — confirm Target Mapping":"");
    });
    public Task AddFilesAsync(IEnumerable<string> paths)=>Run(async token=>{
        var rate=float.TryParse(fps.Text,out var f)&&float.IsFinite(f)&&f>0&&f<=240?f:30;
        foreach(var path in paths){
            token.ThrowIfCancellationRequested();status.Text="Importing "+System.IO.Path.GetFileName(path)+"…";
            var source=await HandEditorPipeline.LoadSourceAsync(path,rate,token);await HandEditorPipeline.MainThread();
            if(source.Scene.Clips.Count==0)throw new InvalidOperationException("The source file contains no animation clips.");
            var preferred=source.Scene.Clips.FirstOrDefault(c=>c.Name.Contains("reload",StringComparison.OrdinalIgnoreCase))??source.Scene.Clips[0];
            foreach(var clip in source.Scene.Clips)rows.Add(new(source,clip){Selected=source.ModelPath is null||ReferenceEquals(clip,preferred)});
        }
        status.Text=$"{rows.Count} source clip(s). Select the clips to convert.";
    });
    void MapTarget()
    {
        if(Target is null)return;var target=Target;var dialog=new HandMappingDialog(this,"Target",target.Skeleton,target.Mapping,target.PalmFrames);
        dialog.Applied=map=>{target.Mapping=map;target.PalmFrames=dialog.PalmFrames;LastBake=Array.Empty<HandBakedClip>();Refresh();};dialog.Show();
    }
    void MapSource(HandSource source){var dialog=new HandMappingDialog(this,System.IO.Path.GetFileName(source.Path),source.Scene.Skeleton,source.Mapping,source.PalmFrames);dialog.Applied=map=>{source.Mapping=map;source.PalmFrames=dialog.PalmFrames;LastBake=Array.Empty<HandBakedClip>();Refresh();};dialog.Show();}
    public Task PreviewAsync(int index)=>Run(async token=>{
        if(Target is null)throw new InvalidOperationException("Choose a target first.");
        var target=Target;var selected=rows[index];var options=MotionOptions();
        var baked=await Task.Run(()=>HandEditorPipeline.Bake(selected.Source,selected.Animation,target,options,token),token);
        await HandEditorPipeline.MainThread();LastBake=new[]{baked};
        var dialog=new HandPreviewDialog(this,target,LastBake);dialog.Confirmed=()=>_=ExportBakedAsync(target,new[]{baked});dialog.Show();
    });
    public Task ConvertAsync()=>Run(async token=>{
        if(Target is null)throw new InvalidOperationException("Choose a target first.");
        var selected=rows.Where(r=>r.Selected).ToArray();if(selected.Length==0)throw new InvalidOperationException("Select at least one source clip.");
        var target=Target;var options=MotionOptions();var baked=new List<HandBakedClip>();
        var profiles=new Dictionary<HandSource,HumanoidHandRetargeter.Calibration.HandRetargetProfile>();
        for(var i=0;i<selected.Length;i++){
            status.Text=$"Retargeting {i+1} / {selected.Length}: {selected[i].Animation.Name}";
            var row=selected[i];baked.Add(await Task.Run(()=>{if(!profiles.TryGetValue(row.Source,out var profile)){profile=HandEditorPipeline.Calibrate(row.Source,target);profiles.Add(row.Source,profile);}return HandEditorPipeline.Bake(row.Source,row.Animation,target,options,token,profile);},token));await HandEditorPipeline.MainThread();
        }
        LastBake=baked;await Export(target,baked,token);
    });
    Task ExportBakedAsync(HandTarget target,IReadOnlyList<HandBakedClip> clips)=>Run(token=>Export(target,clips,token));
    async Task Export(HandTarget target,IReadOnlyList<HandBakedClip> clips,CancellationToken token)
    {
        status.Text="Writing and compiling animations…";
        var result=await HandEditorPipeline.ExportWithReportAsync(target,clips,output.Text,graph.Value,backup.Value,weapon.Value,companion.Value,token);
        await HandEditorPipeline.MainThread();status.Text=$"Done: {clips.Count} clip(s) → {result.ModelPath}";
        if(result.WeaponPrefabs is {Count:>0})status.Text+=$"\nCreated {result.WeaponPrefabs.Count} weapon prefab(s). Paths are under Show details.";
        report.Text=string.Join("\n",result.Changes.Concat(clips.SelectMany(c=>c.Notes)).Distinct())+(result.BackupPath is null?"":"\nBackup: "+result.BackupPath)+(companion.Value?"\nOriginal weapon/camera/IK tracks saved in the source_tracks folder.":"");
        report.Visible=false;details.Text="Show details";details.Visible=true;
    }
    void ToggleDetails(){report.Visible=!report.Visible;details.Text=report.Visible?"Hide details":"Show details";}
    HandMotionOptions MotionOptions()=>new(){SolveArmIk=ik.Value,TransferWristPosition=travel.Value,ScaleWristTravel=scale.Value,PreserveWeaponGrip=grip.Value};
    async Task Run(Func<CancellationToken,Task> work)
    {
        if(busy)return;busy=true;cancellation=new();report.Visible=false;details.Visible=false;Refresh();
        try{await work(cancellation.Token);}
        catch(OperationCanceledException){await HandEditorPipeline.MainThread();status.Text="Cancelled.";}
        catch(Exception error){await HandEditorPipeline.MainThread();status.Text="Action could not be completed.";report.Text=error.Message;report.Visible=true;}
        finally{await HandEditorPipeline.MainThread();busy=false;cancellation.Dispose();cancellation=null;if(IsValid)Refresh();}
    }
    void Refresh()
    {
        add.Enabled=!busy;targets.Enabled=!busy;options.Enabled=!busy;convert.Enabled=!busy&&Target is not null&&rows.Any(r=>r.Selected);
        list.Clear(true);
        foreach(var row in rows){
            var widget=new Widget(this);widget.Layout=Layout.Row();widget.Layout.Margin=new Sandbox.UI.Margin(32,4,8,4);widget.Layout.Spacing=8;
            var selection=widget.Layout.Add(new Checkbox(""){Value=row.Selected,Enabled=!busy});selection.Clicked=()=>{row.Selected=selection.Value;convert.Enabled=!busy&&Target is not null&&rows.Any(r=>r.Selected);};
            widget.Layout.Add(new Label(this){Text=System.IO.Path.GetFileName(row.Source.Path)+" · "+row.Animation.Name},1).SetStyles("font-weight:600;");
            var chip=widget.Layout.Add(new Label(this){Text=row.Source.Mapping.NeedsReview?"Needs review":"Mapped",FixedHeight=20});
            chip.SetStyles($"background-color:{(row.Source.Mapping.NeedsReview?Theme.Yellow:Theme.Green).WithAlpha(.18f).Hex};border-radius:10px;padding:2px 8px;");
            widget.Layout.Add(new Button("Mapping…","device_hub"){Enabled=!busy,Clicked=()=>MapSource(row.Source)});
            widget.Layout.Add(new Button("Preview…","preview"){Enabled=!busy&&Target is not null,Clicked=()=>_=PreviewAsync(rows.IndexOf(row))});
            widget.Layout.Add(new IconButton("close"){Enabled=!busy,ToolTip="Remove this take from the list",OnClick=()=>{rows.Remove(row);Refresh();}});list.Add(widget);
        }
        list.AddStretchCell();
    }
    public override void OnDestroyed(){cancellation?.Cancel();if(ReferenceEquals(instance,this))instance=null;base.OnDestroyed();}
    sealed class Row {public HandSource Source;public Clip Animation;public bool Selected=true;public Row(HandSource source,Clip clip){Source=source;Animation=clip;}}
}
