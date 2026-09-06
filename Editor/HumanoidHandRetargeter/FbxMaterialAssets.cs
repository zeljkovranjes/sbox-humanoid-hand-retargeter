#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HumanoidHandRetargeter.Formats.Fbx;
namespace HumanoidHandRetargeter.Editor;
/// <summary>Resolves relocated FBX texture references and embedded media before any output is written.</summary>
public static class FbxMaterialAssets
{
    public sealed record TextureFile(string Reference,byte[] Bytes,string Extension);
    public sealed record Prepared(IReadOnlyList<FbxMaterialReader.SourceMaterialInfo> Materials,IReadOnlyDictionary<string,TextureFile> Textures,string Signature)
    {
        public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    }
    static readonly string[] Extensions={".png",".tga",".jpg",".jpeg",".dds",".webp"};

    public static Prepared Inspect(string source,byte[] bytes)
    {
        var materials=FbxMaterialReader.Read(bytes);
        var root=FbxTokenizer.Parse(bytes);
        if(materials.Count==0)
            materials.AddRange(root.Child("Objects")?.ChildrenNamed("Geometry").Select(n=>new FbxMaterialReader.SourceMaterialInfo{Name=FbxNode.SplitName(n.Prop<string>(1)).Name})??Enumerable.Empty<FbxMaterialReader.SourceMaterialInfo>());
        var embedded=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach(var video in root.Child("Objects")?.ChildrenNamed("Video")??Enumerable.Empty<FbxNode>())
        {
            if(video.Child("Content")?.Properties.FirstOrDefault() is not byte[] content||content.Length==0)continue;
            foreach(var name in video.Children.Where(n=>n.Name.Equals("RelativeFilename",StringComparison.OrdinalIgnoreCase)||n.Name.Equals("Filename",StringComparison.OrdinalIgnoreCase)).Select(n=>n.Properties.FirstOrDefault()).OfType<string>())
                embedded[Path.GetFileName(name.Replace('\\','/'))]=content;
        }
        var directory=Path.GetDirectoryName(Path.GetFullPath(source))!;
        var candidates=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        var notes=new List<string>();
        void AddFolder(string folder,bool recursive,int priority)
        {
            if(!Directory.Exists(folder))return;
            foreach(var path in Directory.EnumerateFiles(folder,"*",recursive?SearchOption.AllDirectories:SearchOption.TopDirectoryOnly))
                if(Extensions.Contains(Path.GetExtension(path),StringComparer.OrdinalIgnoreCase))candidates.TryAdd(path,priority);
        }
        AddFolder(directory,false,0);
        var ancestor=new DirectoryInfo(directory);
        for(var depth=0;depth<3&&ancestor is not null;depth++,ancestor=ancestor.Parent)
        {
            if(depth==1)AddFolder(ancestor.FullName,false,depth*2);
            foreach(var name in new[]{"Textures","textures",Path.GetFileNameWithoutExtension(source)+".fbm"})AddFolder(Path.Combine(ancestor.FullName,name),true,depth*2+1);
        }
        if(materials.Count==1&&string.IsNullOrWhiteSpace(materials[0].ColorTexture)&&!materials[0].VertexColors)
        {
            var colors=candidates.Keys.Where(IsColorCandidate).ToArray();
            if(colors.Length==1)materials[0].ColorTexture=colors[0];
        }
        if(materials.Count==1)
        {
            string? Sidecar(string channel)
            {
                var matches=candidates.Keys.Where(p=>TextureChannel(p)==channel).ToArray();
                if(matches.Length==0)return null;
                var nearest=matches.Min(p=>candidates[p]);
                matches=matches.Where(p=>candidates[p]==nearest).ToArray();
                return matches.Length==1?matches[0]:null;
            }
            var material=materials[0];
            material.NormalTexture??=Sidecar("normal");
            material.RoughnessTexture??=Sidecar("roughness");
            material.MetalnessTexture??=Sidecar("metalness");
            material.OcclusionTexture??=Sidecar("occlusion");
        }
        var textures=new Dictionary<string,TextureFile>(StringComparer.OrdinalIgnoreCase);
        foreach(var reference in materials.SelectMany(References).Where(p=>!string.IsNullOrWhiteSpace(p)).Select(p=>p!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
        {
            var normalized=reference.Replace('\\','/');var fileName=Path.GetFileName(normalized);byte[] data;string extension;
            if(embedded.TryGetValue(fileName,out var payload)){data=payload;extension=Path.GetExtension(fileName).ToLowerInvariant();}
            else
            {
                var direct=Path.IsPathRooted(normalized)?normalized:Path.GetFullPath(Path.Combine(directory,normalized));
                string? resolved=File.Exists(direct)&&Extensions.Contains(Path.GetExtension(direct),StringComparer.OrdinalIgnoreCase)?direct:null;
                if(resolved is null)
                {
                    var exact=candidates.Keys.Where(p=>Path.GetFileName(p).Equals(fileName,StringComparison.OrdinalIgnoreCase)).ToArray();
                    var matches=exact.Length>0?exact:candidates.Keys.Where(p=>Path.GetFileNameWithoutExtension(p).Equals(Path.GetFileNameWithoutExtension(fileName),StringComparison.OrdinalIgnoreCase)).ToArray();
                    var isColor=materials.Any(m=>string.Equals(m.ColorTexture,reference,StringComparison.OrdinalIgnoreCase));
                    if(matches.Length==0&&isColor&&materials.Count==1)
                        matches=candidates.Keys.Where(IsColorCandidate).ToArray();
                    if(matches.Length==0&&!isColor&&materials.Count==1)
                    {
                        var material=materials[0];
                        var channel=material.NormalTexture==reference?"normal":material.RoughnessTexture==reference?"roughness"
                            :material.MetalnessTexture==reference?"metalness":material.OcclusionTexture==reference?"occlusion"
                            :material.EmissiveTexture==reference?"emissive":"opacity";
                        matches=candidates.Keys.Where(p=>TextureChannel(p)==channel).ToArray();
                    }
                    if(matches.Length==0)
                    {
                        if(materials.Any(m=>m.OpacityTexture==reference))throw new FileNotFoundException($"Missing opacity texture '{reference}'. Supply the authored image to preserve transparency.");
                        notes.Add(isColor?$"Color texture '{reference}' was not supplied; using the authored material color or a neutral default."
                            :$"Optional texture '{reference}' was not supplied; using a neutral material default.");
                        // Missing auxiliary maps use the material writer's neutral defaults.
                        foreach(var material in materials)
                        {
                            if(string.Equals(material.ColorTexture,reference,StringComparison.OrdinalIgnoreCase))material.ColorTexture=null;
                            if(material.NormalTexture==reference)material.NormalTexture=null;
                            if(material.RoughnessTexture==reference)material.RoughnessTexture=null;
                            if(material.MetalnessTexture==reference)material.MetalnessTexture=null;
                            if(material.OcclusionTexture==reference)material.OcclusionTexture=null;
                            if(material.EmissiveTexture==reference)material.EmissiveTexture=null;
                            if(material.OpacityTexture==reference)material.OpacityTexture=null;
                        }
                        continue;
                    }
                    var priority=matches.Min(p=>candidates[p]);
                    matches=matches.Where(p=>candidates[p]==priority).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).ToArray();
                    var distinct=matches.GroupBy(p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
                    if(distinct.Length!=1)throw new InvalidOperationException($"Multiple different textures match '{reference}'. Keep one matching file beside the FBX to resolve the ambiguity.");
                    resolved=matches[0];
                }
                data=File.ReadAllBytes(resolved);extension=Path.GetExtension(resolved).ToLowerInvariant();
            }
            if(!Extensions.Contains(extension))throw new InvalidOperationException($"Texture '{reference}' needs a PNG, TGA, JPG or DDS export.");
            textures.Add(reference,new(reference,data,extension));
        }
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);hash.AppendData(bytes);
        foreach(var pair in textures.OrderBy(p=>p.Key,StringComparer.Ordinal)){hash.AppendData(Encoding.UTF8.GetBytes(pair.Key));hash.AppendData(pair.Value.Bytes);}
        return new(materials,textures,Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()[..16]){Notes=notes};
    }
    static IEnumerable<string?> References(FbxMaterialReader.SourceMaterialInfo material)
        =>new[]{material.ColorTexture,material.NormalTexture,material.RoughnessTexture,material.MetalnessTexture,material.OcclusionTexture,material.EmissiveTexture,material.OpacityTexture};

    static bool IsColorCandidate(string path)
        =>TextureChannel(path)=="color";

    // Common map suffixes shared with humanoid-retargeter's sidecar matching.
    static string TextureChannel(string path)
    {
        var stem=Path.GetFileNameWithoutExtension(path);
        stem=System.Text.RegularExpressions.Regex.Replace(stem,"([a-z0-9])([A-Z])","$1 $2");
        stem=System.Text.RegularExpressions.Regex.Replace(stem,"([A-Z])([A-Z][a-z])","$1 $2");
        var tokens=System.Text.RegularExpressions.Regex.Split(stem.ToLowerInvariant(),"[^a-z0-9]+");
        if(tokens.Any(t=>t is "n" or "nm" or "nrm" or "nor" or "norm" or "normal" or "normalmap" or "bump"))return "normal";
        if(tokens.Any(t=>t is "r" or "rough" or "roughness"))return "roughness";
        if(tokens.Any(t=>t is "m" or "metal" or "metallic" or "metalness"))return "metalness";
        if(tokens.Any(t=>t is "ao" or "occlusion" or "ambientocclusion"))return "occlusion";
        if(tokens.Any(t=>t is "e" or "emissive" or "emission" or "glow"))return "emissive";
        if(tokens.Any(t=>t is "a" or "alpha" or "opacity" or "trans" or "transparency"))return "opacity";
        if(tokens.Any(t=>t is "height" or "displacement" or "orm" or "mask" or "g" or "gloss" or "glossiness"))return "other";
        return "color";
    }

}
