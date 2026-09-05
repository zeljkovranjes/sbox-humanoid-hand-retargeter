// Tokenization reused from humanoid-retargeter 26084c96c3fc870aaf9a5bd798de063ce2fd62df.
using System.Text;
namespace HumanoidHandRetargeter.Mapping;
internal static class BoneNameTokens
{
    internal static List<string> Tokenize(string name)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char previous = '\0';
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                previous = '\0';
                continue;
            }
            if (current.Length > 0)
            {
                var boundary = char.IsDigit(c) != char.IsDigit(previous)
                    || (char.IsUpper(c) && char.IsLower(previous))
                    // Acronym/single-side prefix before a Pascal word: LThumb → L, Thumb;
                    // FBXNode → FBX, Node. Without this, one-joint BVH thumbs lose both
                    // their side and their otherwise unambiguous finger name.
                    || (char.IsUpper(c) && char.IsUpper(previous)
                        && i + 1 < name.Length && char.IsLower(name[i + 1]));
                if (boundary)
                    Flush();
            }
            current.Append(char.ToLowerInvariant(c));
            previous = c;
        }
        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
    }

}