#nullable enable
using System;
using System.Linq;
using Editor;
using Sandbox;
namespace HumanoidHandRetargeter.Editor;
public static class HandAssetContextMenu
{
    [Event("asset.contextmenu",Priority=60)]
    public static void Add(AssetContextMenu context)
    {
        var files=context.SelectedList.Where(a=>new[]{".fbx",".vmdl"}.Contains(System.IO.Path.GetExtension(a.AbsolutePath),StringComparer.OrdinalIgnoreCase)).Select(a=>a.AbsolutePath).ToArray();
        if(files.Length==0)return;
        context.Menu.AddOption("Retarget Hand Animations…","sync_alt",()=>{var window=HandRetargetWindow.Open();if(window is not null)_=window.AddFilesAsync(files);});
    }
}
