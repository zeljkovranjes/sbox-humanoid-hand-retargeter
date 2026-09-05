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
    public sealed record Prepared(IReadOnlyList<FbxMaterialReader.SourceMaterialInfo> Materials,IReadOnlyDictionary<string,TextureFile> Textures,string Signature);
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
        var candidates=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddFolder(string folder,bool recursive)
        {
            if(!Directory.Exists(folder))return;
            foreach(var path in Directory.EnumerateFiles(folder,"*",recursive?SearchOption.AllDirectories:SearchOption.TopDirectoryOnly))
                if(Extensions.Contains(Path.GetExtension(path),StringComparer.OrdinalIgnoreCase))candidates.Add(path);
        }
        AddFolder(directory,false);
        var ancestor=new DirectoryInfo(directory);
        for(var depth=0;depth<3&&ancestor is not null;depth++,ancestor=ancestor.Parent)
            foreach(var name in new[]{"Textures","textures",Path.GetFileNameWithoutExtension(source)+".fbm"})AddFolder(Path.Combine(ancestor.FullName,name),true);
        var textures=new Dictionary<string,TextureFile>(StringComparer.OrdinalIgnoreCase);
        foreach(var reference in materials.SelectMany(References).Where(p=>!string.IsNullOrWhiteSpace(p)).Select(p=>p!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var normalized=reference.Replace('\\','/');var fileName=Path.GetFileName(normalized);byte[] data;string extension;
            if(embedded.TryGetValue(fileName,out var payload)){data=payload;extension=Path.GetExtension(fileName).ToLowerInvariant();}
            else
            {
                var direct=Path.IsPathRooted(normalized)?normalized:Path.GetFullPath(Path.Combine(directory,normalized));
                string? resolved=File.Exists(direct)&&Extensions.Contains(Path.GetExtension(direct),StringComparer.OrdinalIgnoreCase)?direct:null;
                if(resolved is null)
                {
                    var exact=candidates.Where(p=>Path.GetFileName(p).Equals(fileName,StringComparison.OrdinalIgnoreCase)).ToArray();
                    var matches=exact.Length>0?exact:candidates.Where(p=>Path.GetFileNameWithoutExtension(p).Equals(Path.GetFileNameWithoutExtension(fileName),StringComparison.OrdinalIgnoreCase)).ToArray();
                    if(matches.Length==0)throw new FileNotFoundException($"Missing authored texture '{reference}'. Place it in a Textures folder beside the FBX or its parent folder before importing.");
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
        return new(materials,textures,Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()[..16]);
    }
    static IEnumerable<string?> References(FbxMaterialReader.SourceMaterialInfo material)
        =>new[]{material.ColorTexture,material.NormalTexture,material.RoughnessTexture,material.MetalnessTexture,material.OcclusionTexture,material.EmissiveTexture,material.OpacityTexture};

}
