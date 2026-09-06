#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Editor;
using HumanoidHandRetargeter.Formats.Fbx;
using Sandbox;
namespace HumanoidHandRetargeter.Editor;

/// <summary>Preserves FBX material links and resolves moved texture sets without guessing a different material.</summary>
public static class HandMaterialImport
{
    public static IReadOnlyDictionary<string,string> Write(FbxMaterialAssets.Prepared prepared,string folder)
    {
        var paths=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var (reference,file) in prepared.Textures)
        {
            var stem=Convert.ToHexString(SHA256.HashData(file.Bytes)).ToLowerInvariant()[..16];
            var relative=folder+"/textures/"+stem+file.Extension;var absolute=Path.Combine(HandEditorPipeline.Assets,relative);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);if(!File.Exists(absolute))File.WriteAllBytes(absolute,file.Bytes);
            if(file.Extension is ".jpeg" or ".webp")
            {
                using var bitmap=SkiaSharp.SKBitmap.Decode(file.Bytes)??throw new InvalidOperationException("Cannot decode "+reference);
                using var image=SkiaSharp.SKImage.FromBitmap(bitmap);using var encoded=image.Encode(SkiaSharp.SKEncodedImageFormat.Png,100);
                relative=folder+"/textures/"+stem+".png";absolute=Path.Combine(HandEditorPipeline.Assets,relative);if(!File.Exists(absolute))File.WriteAllBytes(absolute,encoded.ToArray());
            }
            AssetSystem.RegisterFile(absolute);paths.Add(reference,relative);
        }
        var remaps=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var material in prepared.Materials)
        {
            var materialId=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.Name))).ToLowerInvariant()[..8];
            var path=folder+"/materials/"+HandEditorPipeline.SafeName(material.Name).ToLowerInvariant()+"_"+materialId+".vmat";
            var absolute=Path.Combine(HandEditorPipeline.Assets,path);Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            if(!File.Exists(absolute))
            {
                var text=new StringBuilder("// Imported hand material. Authored edits are preserved.\nLayer0\n{\n");
                text.AppendLine(material.VertexColors&&material.ColorTexture is null?"shader \"shaders/vertex_color.shader\"":"shader \"shaders/complex.shader\"");
                void Texture(string parameter,string? reference,string? fallback=null){var value=reference is null?fallback:paths[reference];if(value is not null)text.AppendLine(parameter+" \""+value+"\"");}
                Texture("TextureColor",material.ColorTexture,"materials/default/default_color.tga");
                Texture("TextureNormal",material.NormalTexture,"materials/default/default_normal.tga");
                Texture("TextureRoughness",material.RoughnessTexture,"materials/default/default_rough.tga");
                Texture("TextureAmbientOcclusion",material.OcclusionTexture);
                if(material.MetalnessTexture is not null){text.AppendLine("F_METALNESS_TEXTURE 1");Texture("TextureMetalness",material.MetalnessTexture);}
                if(material.EmissiveTexture is not null){text.AppendLine("F_SELF_ILLUM 1");Texture("TextureSelfIllumMask",material.EmissiveTexture);}
                if(material.DoubleSided)text.AppendLine("F_RENDER_BACKFACES 1");
                var opacity=material.OpacityTexture is {} opacityReference?paths[opacityReference]:null;
                var colorPath=material.ColorTexture is {} colorReference?paths[colorReference]:null;
                if((material.AlphaTest||material.Translucent)&&opacity is null)opacity=colorPath;
                if(opacity is not null&&string.Equals(opacity,colorPath,StringComparison.OrdinalIgnoreCase))opacity=ExtractPackedAlpha(opacity);
                if(opacity is not null)
                {
                    if(material.AlphaTest)
                    {
                        text.AppendLine("F_ALPHA_TEST 1");
                        text.AppendLine(FormattableString.Invariant($"g_flAlphaTestReference {material.AlphaCutoff:R}"));
                    }
                    else text.AppendLine("F_TRANSLUCENT 1");
                    text.AppendLine("TextureTranslucency \""+opacity+"\"");
                }
                if(material.ColorTexture is null && material.ColorFactor is {} color)text.AppendLine(FormattableString.Invariant($"g_vColorTint \"[{color.X:R} {color.Y:R} {color.Z:R} 1]\""));
                text.AppendLine("}");File.WriteAllText(absolute,text.ToString());
            }
            AssetSystem.RegisterFile(absolute);remaps.Add(material.Name.ToLowerInvariant()+".vmat",path);
        }
        return remaps;
    }

    // Same packed-alpha handling as humanoid-retargeter: complex.shader expects
    // grayscale opacity, not the RGB channels of a packed color texture.
    static string? ExtractPackedAlpha(string relative)
    {
        using var bitmap=SkiaSharp.SKBitmap.Decode(Path.Combine(HandEditorPipeline.Assets,relative))
            ??throw new InvalidOperationException("Cannot decode packed opacity texture '"+relative+"'. Supply a PNG export.");
        var pixels=bitmap.Pixels;
        if(pixels.All(p=>p.Alpha==255))return null;
        using var mask=new SkiaSharp.SKBitmap(bitmap.Width,bitmap.Height,SkiaSharp.SKColorType.Rgba8888,SkiaSharp.SKAlphaType.Opaque);
        mask.Pixels=pixels.Select(p=>new SkiaSharp.SKColor(p.Alpha,p.Alpha,p.Alpha)).ToArray();
        using var image=SkiaSharp.SKImage.FromBitmap(mask);
        using var data=image.Encode(SkiaSharp.SKEncodedImageFormat.Png,100);
        var output=relative+"_alpha.png";
        var absolute=Path.Combine(HandEditorPipeline.Assets,output);
        if(!File.Exists(absolute))File.WriteAllBytes(absolute,data.ToArray());
        AssetSystem.RegisterFile(absolute);
        return output;
    }
}
