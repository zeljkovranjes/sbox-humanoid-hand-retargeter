#nullable enable
using HumanoidHandRetargeter.Validation;
using SkeletonModel = HumanoidHandRetargeter.Skeleton.Skeleton;

namespace HumanoidHandRetargeter.Mapping;

/// <summary>
/// Name candidates constrained by wrist ancestry. Reuses legacy name tokenization;
/// deliberately replaces its full-body confidence and fixed phalanx slots.
/// Unnamed/ambiguous anatomy is proposed for review, never silently accepted.
/// </summary>
public static class HandRigDetector
{
    public static HandMappingResult Detect(SkeletonModel skeleton,
        IEnumerable<HandRigDefinition>? manualOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        var names = skeleton.Bones.Select(b => new NameInfo(b.Name)).ToArray();
        var hands = new List<HandRigDefinition>();
        var candidates = new List<HandMappingCandidate>();
        var issues = new List<RigIssue>();
        var review = false;
        var overrides = (manualOverrides ?? Array.Empty<HandRigDefinition>()).ToArray();
        if (overrides.Any(h => h is null))
            throw new ArgumentException("Manual overrides cannot contain a null hand.", nameof(manualOverrides));

        foreach (var side in new[] { HandSide.Left, HandSide.Right })
        {
            var manual = overrides.Where(h => h.Side == side).ToArray();
            if (manual.Length > 0)
            {
                hands.AddRange(manual);
                foreach (var hand in manual)
                {
                    Add(side, "Wrist", new[] { hand.Wrist }, 1f, "Manual mapping", true);
                    foreach (var (role, bone) in new[] { ("Clavicle", hand.Clavicle), ("UpperArm", hand.UpperArm), ("Forearm", hand.Forearm) })
                        if (bone is int b) Add(side, role, new[] { b }, 1f, "Manual mapping", true);
                    foreach (var digit in hand.Digits.Where(d => d is not null))
                        Add(side, digit.Role.ToString(), digit.Bones, 1f, "Manual mapping", true);
                    foreach (var helper in hand.TwistOrHelperBones)
                        Add(side, "Helper", new[] { helper }, 1f, "Manual mapping", true);
                }
                continue;
            }
            var wrists = skeleton.Bones.Where(b => names[b.Index].IsWrist && !Blocked(b.Index)
                && SideOf(b.Index) == side).Select(b => b.Index).ToArray();
            if (wrists.Length == 0) continue; // A one-sided rig is valid.
            if (wrists.Length > 1)
            {
                foreach (var wrist in wrists)
                    Add(side, "Wrist", new[] { wrist }, .5f, "Multiple wrist candidates on this side; choose one manually", false);
                issues.Add(new("ambiguous-wrist", $"Choose the {side} wrist in Hand Mapping.", side));
                continue;
            }
            var selected = wrists[0];
            Add(side, "Wrist", new[] { selected }, .95f, "Wrist name and side agree", true);
            var digits = new List<DigitChain>();
            var seenRoles = new HashSet<DigitRole>();
            foreach (var root in DigitRoots(selected))
            {
                var chain = new List<int>();
                int? meta = null, tip = null;
                var current = root;
                var chainReview = false;
                while (true)
                {
                    var info = names[current];
                    if (info.IsTip)
                    {
                        tip = current;
                        if (skeleton.ChildrenOf(current).Count != 0) chainReview = true;
                        break;
                    }
                    if (info.IsMeta && current == root) meta = current;
                    else chain.Add(current);
                    var next = JointChildren(current).ToArray();
                    if (next.Length == 0) break;
                    if (next.Length > 1) { chainReview = true; break; }
                    current = next[0];
                }
                var role = names[root].Digit;
                // A palm/metacarpal may be generically named, but its child's digit is explicit.
                if (role is null && meta.HasValue && chain.Count > 0) role = names[chain[0]].Digit;
                var chainSideMismatch = chain.Any(b => names[b].Side is HandSide explicitSide && explicitSide != side);
                var chainRoleMismatch = role.HasValue && chain.Any(b => names[b].Digit is DigitRole namedRole && namedRole != role);
                if (chainSideMismatch || chainRoleMismatch || chainReview || chain.Count == 0)
                {
                    Add(side, role?.ToString() ?? "Digit", chain, .3f,
                        "Branched, empty or side-inconsistent digit chain; correct the mapping", false);
                    review = true;
                    continue;
                }
                if (role.HasValue && !seenRoles.Add(role.Value))
                {
                    Add(side, role.Value.ToString(), chain, .4f, "Duplicate anatomical digit candidate; choose the intended chain", false);
                    review = true;
                    continue;
                }
                var digit = new DigitChain(role ?? DigitRole.Extra, chain, meta, tip,
                    role is null ? skeleton[root].Name : "");
                digits.Add(digit);
                var confidence = role.HasValue ? .9f : .4f;
                Add(side, role?.ToString() ?? "Extra:" + skeleton[root].Name, digit.Bones, confidence,
                    role.HasValue ? "Named digit follows this wrist's hierarchy; segment numbering is not used for side"
                        : "Unlabelled digit chain; assign an anatomical role or confirm an extra digit", true);
                if (!role.HasValue) review = true;
            }

            var ancestors = new List<int>();
            for (var p = skeleton[selected].ParentIndex; p >= 0; p = skeleton[p].ParentIndex)
                ancestors.Add(p);
            int? Find(params string[] aliases) => ancestors
                .Where(b => !names[b].IsHelper && !Blocked(b) && aliases.Any(a => names[b].Core.EndsWith(a, StringComparison.Ordinal)))
                .Select(b => (int?)b).FirstOrDefault();
            var forearm = Find("forearm", "lowerarm", "armlower", "elbow");
            var upperArm = Find("upperarm", "armupper", "uparm");
            var clavicle = Find("clavicle", "collar");
            // Prototype calls upper arm Shoulder, Mixamo calls clavicle Shoulder.
            if (upperArm is null && clavicle.HasValue) upperArm = Find("shoulder");
            upperArm ??= ancestors.Where(b => names[b].Core.EndsWith("arm", StringComparison.Ordinal)
                && b != forearm && !names[b].IsHelper && !Blocked(b)).Select(b => (int?)b).FirstOrDefault();
            clavicle ??= Find("shoulder");
            if (clavicle == upperArm) clavicle = null;
            foreach (var (role, bone) in new[] { ("Clavicle", clavicle), ("UpperArm", upperArm), ("Forearm", forearm) })
                if (bone is int b)
                {
                    var mismatch = names[b].ConflictingSides || (SideOf(b) is HandSide namedSide && namedSide != side);
                    Add(side, role, new[] { b }, mismatch ? .3f : .9f,
                        mismatch ? "Arm name conflicts with the wrist side; correct the mapping" : "Named joint on the wrist ancestor chain", true);
                    review |= mismatch;
                }
            var armRoot = clavicle ?? upperArm ?? forearm ?? selected;
            var helpers = skeleton.Bones.Where(b => names[b.Index].IsHelper && !Blocked(b.Index)
                && skeleton.DescendsFrom(b.Index, armRoot)).Select(b => b.Index).ToArray();
            foreach (var helper in helpers)
                Add(side, "Helper", new[] { helper }, .9f, "Explicit twist/helper name in this arm hierarchy", true);
            hands.Add(new(side, selected, digits, clavicle, upperArm, forearm, helpers));
        }

        // Unsupported enum values must reach validation instead of being silently dropped.
        hands.AddRange(overrides.Where(h => !Enum.IsDefined(typeof(HandSide), h.Side)));
        foreach (var bone in skeleton.Bones.Where(b => names[b.Index].IsWrist && !Blocked(b.Index) && SideOf(b.Index) is null
            && !hands.Any(h => h.Wrist == b.Index)))
        {
            Add(null, "Wrist", new[] { bone.Index }, .4f, "Wrist name found but side is unknown or conflicting", false);
            review = true;
        }
        if (hands.Count == 0 && candidates.Count == 0)
        {
            // Topology hints only: without a known side/frame, geometry must not fabricate left/right.
            foreach (var bone in skeleton.Bones.Where(b => !Blocked(b.Index) &&
                skeleton.ChildrenOf(b.Index).Count(c => !Blocked(c) && skeleton.ChildrenOf(c).Count > 0) >= 2))
                Add(null, "Wrist", new[] { bone.Index }, .2f, "Branching hierarchy may be a palm; confirm side and wrist manually", false);
            review = true;
        }
        issues.AddRange(HandRigValidator.Validate(skeleton, hands));
        return new(hands, candidates, issues, review);

        void Add(HandSide? side, string role, IEnumerable<int> bones, float confidence, string reason, bool selected)
            => candidates.Add(new(side, role, Array.AsReadOnly(bones.ToArray()), confidence, reason, selected));

        // The viewmodel may own the whole hierarchy: a weapon-named ancestor is not
        // evidence that an explicitly named child wrist is a mechanical weapon bone.
        bool Blocked(int bone) => names[bone].IsNonHandTrack;
        HandSide? SideOf(int bone)
        {
            for (var b = bone; b >= 0; b = skeleton[b].ParentIndex)
            {
                if (names[b].ConflictingSides) return null;
                if (names[b].Side.HasValue) return names[b].Side;
            }
            return null;
        }
        IEnumerable<int> DigitRoots(int parent)
        {
            foreach (var child in JointChildren(parent))
            {
                if (!names[child].IsTip) yield return child;
            }
        }
        IEnumerable<int> JointChildren(int parent)
        {
            foreach (var child in skeleton.ChildrenOf(parent))
            {
                if (Blocked(child)) continue;
                if (names[child].IsHelper)
                {
                    foreach (var nested in JointChildren(child)) yield return nested;
                }
                else yield return child;
            }
        }
    }

    private sealed class NameInfo
    {
        public string Core { get; }
        public HandSide? Side { get; }
        public bool ConflictingSides { get; }
        public DigitRole? Digit { get; }
        public bool IsWrist { get; }
        public bool IsHelper { get; }
        public bool IsNonHandTrack { get; }
        public bool IsMeta { get; }
        public bool IsTip { get; }

        public NameInfo(string name)
        {
            var stripped = name[(name.LastIndexOf(':') + 1)..];
            var hash = stripped.LastIndexOf('#');
            if (hash >= 0 && stripped[(hash + 1)..].All(char.IsDigit)) stripped = stripped[..hash];
            var tokens = BoneNameTokens.Tokenize(stripped);
            bool Has(params string[] words) => tokens.Any(t => words.Contains(t, StringComparer.Ordinal));
            var left = Has("l", "left", "lft");
            var right = Has("r", "right", "rgt");
            // Case-only changes must not erase a wrist's side when camel-case boundaries disappear.
            var compact = string.Concat(tokens.Where(t => !t.All(char.IsDigit)));
            foreach (var joint in new[] { "hand", "wrist" })
            {
                left |= compact.EndsWith("left" + joint, StringComparison.Ordinal) || compact.EndsWith(joint + "left", StringComparison.Ordinal)
                    || compact.EndsWith(joint + "l", StringComparison.Ordinal) || compact == "l" + joint;
                right |= compact.EndsWith("right" + joint, StringComparison.Ordinal) || compact.EndsWith(joint + "right", StringComparison.Ordinal)
                    || compact.EndsWith(joint + "r", StringComparison.Ordinal) || compact == "r" + joint;
            }
            ConflictingSides = left && right;
            Side = left == right ? null : left ? HandSide.Left : HandSide.Right;
            Core = string.Concat(tokens.Where(t => !new[] { "l", "left", "lft", "r", "right", "rgt", "def", "org", "mch", "stretch" }.Contains(t)
                && !t.All(char.IsDigit)));
            IsHelper = Has("twist", "twistctrl", "roll", "share", "sharebone", "helper", "hlp", "corrective", "control", "ctrl");
            IsNonHandTrack = Has("weapon", "camera", "ik", "ikrule", "hold", "socket", "attachment", "magazine", "bolt", "slide", "trigger")
                || compact.StartsWith("weapon", StringComparison.Ordinal) || compact.StartsWith("camera", StringComparison.Ordinal);
            IsMeta = Has("meta", "metacarpal", "carpal");
            IsTip = Has("tip", "end", "nub", "endmarker");
            bool DigitName(params string[] aliases) => Has(aliases)
                || aliases.Any(a => Core.EndsWith(a, StringComparison.Ordinal) || Core.EndsWith(a + "finger", StringComparison.Ordinal));
            Digit = DigitName("thumb") ? DigitRole.Thumb : DigitName("index", "forefinger") ? DigitRole.Index
                : DigitName("middle", "mid") ? DigitRole.Middle : DigitName("ring") ? DigitRole.Ring
                : DigitName("pinky", "pinkie", "little") ? DigitRole.Pinky : null;
            IsWrist = !IsHelper && !IsNonHandTrack && !IsTip && Digit is null
                && new[] { "hand", "wrist", "handl", "handr", "wristl", "wristr", "handleft", "handright", "wristleft", "wristright" }
                    .Any(suffix => Core.EndsWith(suffix, StringComparison.Ordinal));
        }
    }
}
