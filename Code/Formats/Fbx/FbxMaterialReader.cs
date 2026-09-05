#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Linq;
namespace HumanoidHandRetargeter.Formats.Fbx;
// Material-link parsing reused from the audited legacy editor pipeline.
public static class FbxMaterialReader
{
	public sealed class SourceMaterialInfo
	{
		public string Name { get; init; } = "";
		public string? ColorTexture { get; set; }
		public System.Numerics.Vector3? ColorFactor { get; set; }
		public bool VertexColors { get; set; }
		public string? NormalTexture { get; set; }
		public string? RoughnessTexture { get; set; }
		public string? MetalnessTexture { get; set; }
		public string? OcclusionTexture { get; set; }
		public string? EmissiveTexture { get; set; }
		public string? OpacityTexture { get; set; }
		public bool AlphaTest { get; set; }
		public bool Translucent { get; set; }
		public bool DoubleSided { get; set; }
		public float AlphaCutoff { get; set; } = 0.5f;
	}

	/// <summary>Reads the material→texture links authored in an FBX. Matching through
	/// object IDs makes texture filenames irrelevant (TrumpLPmat → tumpLPcolors.png).</summary>
	public static List<SourceMaterialInfo> Read( byte[] data )
	{
		var root = HumanoidHandRetargeter.Formats.Fbx.FbxTokenizer.Parse( data );
		var objects = root.Child( "Objects" );
		var connections = root.Child( "Connections" );
		var materials = new Dictionary<long, SourceMaterialInfo>();
		var textures = new Dictionary<long, string>();
		var videos = new Dictionary<long, string>();
		var doubleSidedModels = new HashSet<long>();

		if ( objects is not null )
		{
			foreach ( var node in objects.Children )
			{
				if ( node.Properties.Count < 2 || node.Properties[0] is not (long or int)
					|| node.Properties[1] is not string rawName )
					continue;
				var id = node.Prop<long>( 0 );
				if ( node.Name == "Model" && string.Equals(
					node.Child( "Culling" )?.Properties.FirstOrDefault() as string,
					"CullingOff", StringComparison.OrdinalIgnoreCase ) )
					doubleSidedModels.Add( id );
				if ( node.Name == "Material" )
				{
					materials[id] = new SourceMaterialInfo
					{
						Name = HumanoidHandRetargeter.Formats.Fbx.FbxNode.SplitName( rawName ).Name,
						ColorFactor = HumanoidHandRetargeter.Formats.Fbx.FbxMaterialColor.Read( node ),
					};
				}
				else if ( node.Name is "Texture" or "Video" )
				{
					var file = node.Children.FirstOrDefault( child =>
						child.Name.Equals( "RelativeFilename", StringComparison.OrdinalIgnoreCase ) )
						?? node.Children.FirstOrDefault( child =>
							child.Name.Equals( "FileName", StringComparison.OrdinalIgnoreCase )
							|| child.Name.Equals( "Filename", StringComparison.OrdinalIgnoreCase ) );
					if ( file?.Properties.FirstOrDefault() is string path )
					{
						if ( node.Name == "Texture" ) textures[id] = path;
						else videos[id] = path;
					}
				}
			}
		}

		if ( connections is null )
			return materials.Values.ToList();

		var coloredGeometry = objects?.Children.Where( n => n.Name == "Geometry"
			&& HumanoidHandRetargeter.Formats.Fbx.FbxMaterialColor.HasVertexColors( n ) )
			.Select( n => n.Prop<long>( 0 ) ).ToHashSet() ?? new HashSet<long>();
		var coloredModels = connections.ChildrenNamed( "C" )
			.Where( n => n.Properties.Count >= 3 && n.Properties[0] is "OO"
				&& n.Properties[1] is long or int && n.Properties[2] is long or int
				&& coloredGeometry.Contains( n.Prop<long>( 1 ) ) )
			.Select( n => n.Prop<long>( 2 ) ).ToHashSet();

		// Video objects commonly carry the only usable filename and parent a Texture.
		foreach ( var connection in connections.ChildrenNamed( "C" ) )
		{
			if ( connection.Properties.Count < 3 || connection.Properties[0] is not string kind
				|| kind != "OO" || connection.Properties[1] is not (long or int)
				|| connection.Properties[2] is not (long or int) )
				continue;
			var source = connection.Prop<long>( 1 );
			var target = connection.Prop<long>( 2 );
			if ( videos.TryGetValue( source, out var file ) && !textures.ContainsKey( target ) )
				textures[target] = file;
		}

		foreach ( var connection in connections.ChildrenNamed( "C" ) )
		{
			if ( connection.Properties.Count < 3 || connection.Properties[0] is not string kind
				|| connection.Properties[1] is not (long or int)
				|| connection.Properties[2] is not (long or int) )
				continue;
			var source = connection.Prop<long>( 1 );
			var target = connection.Prop<long>( 2 );
			// FBX stores sidedness on the mesh model, not its material.
			if ( kind == "OO" && coloredModels.Contains( target )
				&& materials.TryGetValue( source, out var coloredMaterial ) )
				coloredMaterial.VertexColors = true;
			if ( kind == "OO" && doubleSidedModels.Contains( target )
				&& materials.TryGetValue( source, out var boundMaterial ) )
				boundMaterial.DoubleSided = true;
			if ( !textures.TryGetValue( source, out var file )
				|| !materials.TryGetValue( target, out var material ) )
				continue;
			var channel = kind == "OP" && connection.Properties.Count >= 4
				&& connection.Properties[3] is string property ? property : "DiffuseColor";
			if ( channel.Contains( "transparent", StringComparison.OrdinalIgnoreCase )
				|| channel.Contains( "transparency", StringComparison.OrdinalIgnoreCase )
				|| channel.Contains( "opacity", StringComparison.OrdinalIgnoreCase )
				|| channel.Contains( "alpha", StringComparison.OrdinalIgnoreCase ) )
			{
				material.OpacityTexture ??= file;
				material.Translucent = true;
			}
			else if ( channel.Contains( "normal", StringComparison.OrdinalIgnoreCase )
				|| channel.Contains( "bump", StringComparison.OrdinalIgnoreCase ) )
				material.NormalTexture ??= file;
			else if ( channel.Contains( "rough", StringComparison.OrdinalIgnoreCase )
				|| channel.Contains( "gloss", StringComparison.OrdinalIgnoreCase ) )
				material.RoughnessTexture ??= file;
			else if ( channel.Contains( "metal", StringComparison.OrdinalIgnoreCase ) )
				material.MetalnessTexture ??= file;
			else if ( channel.Contains( "occlusion", StringComparison.OrdinalIgnoreCase )
				|| channel.Contains( "ambient", StringComparison.OrdinalIgnoreCase ) )
				material.OcclusionTexture ??= file;
			else if ( channel.Contains( "emissive", StringComparison.OrdinalIgnoreCase )
				|| channel.Contains( "emission", StringComparison.OrdinalIgnoreCase ) )
				material.EmissiveTexture ??= file;
			else if ( channel.Contains( "diffuse", StringComparison.OrdinalIgnoreCase )
				|| channel.Contains( "color", StringComparison.OrdinalIgnoreCase ) )
				material.ColorTexture ??= file;
		}
		return materials.Values.ToList();
	}

}
