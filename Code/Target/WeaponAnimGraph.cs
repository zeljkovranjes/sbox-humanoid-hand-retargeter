#nullable enable
namespace HumanoidHandRetargeter.Target;

public enum WeaponAction { Idle, Fire, Reload, ReloadEmpty, Deploy, Holster }
public sealed record WeaponGraphClip(WeaponAction Action,string Sequence);

/// <summary>Deterministic weapon action graph. One evaluated stream drives weapon and custom hand bones.</summary>
public static class WeaponAnimGraph
{
    public static WeaponAction? Classify(string name)
    {
        var value=name.ToLowerInvariant();
        if(value.EndsWith("_delta")||value.Contains("additive"))return null;
        if(value.StartsWith("reload"))return value.Contains("empty")?WeaponAction.ReloadEmpty:WeaponAction.Reload;
        if(value.StartsWith("deploy"))return WeaponAction.Deploy;
        if(value.StartsWith("holster"))return WeaponAction.Holster;
        if(value.StartsWith("idle"))return WeaponAction.Idle;
        if(value is "fire" or "shoot" or "attack"||value.StartsWith("fire_")||value.StartsWith("shoot_")||value.StartsWith("attack_"))return WeaponAction.Fire;
        return null;
    }

    public static string Create(string modelPath,IReadOnlyList<WeaponGraphClip> clips)
    {
        var actions=clips.ToDictionary(c=>c.Action);
        if(!actions.ContainsKey(WeaponAction.Idle))throw new ArgumentException("A weapon graph requires an idle sequence.");
        foreach(var clip in clips)ArgumentException.ThrowIfNullOrWhiteSpace(clip.Sequence);
        var doc=Kv3.Parse(HandAnimGraph.Create(actions[WeaponAction.Idle].Sequence,modelPath));
        var root=(KvObject)doc.Root;
        var nodes=(KvArray)((KvObject)root["m_nodeManager"])["m_nodes"];
        var rootNode=(KvObject)((KvObject)nodes.Items[0])["value"];
        rootNode["m_inputConnection"]=Connection(100);
        var keptRoot=nodes.Items[0];nodes.Items.Clear();nodes.Items.Add(keptRoot);
        var parameters=new[]{"b_attack","b_reload","b_empty","b_deploy","b_holster","b_deploy_skip"};
        var ids=parameters.Select((p,i)=>(p,id:500+i)).ToDictionary(p=>p.p,p=>p.id);
        ((KvObject)root["m_pParameterList"])["m_Parameters"]=Array(parameters.Select(p=>Object(
            ("_class",Text("CBoolAnimParameter")),("m_name",Text(p)),("m_id",Id(ids[p])),
            ("m_previewButton",Text("ANIMPARAM_BUTTON_NONE")),("m_bUseMostRecentValue",Bool(false)),
            ("m_bAutoReset",Bool(p is "b_attack" or "b_reload" or "b_deploy")),("m_bDefaultValue",Bool(false)))));
        KvObject Condition(string parameter,bool value=true)=>Object(("_class",Text("CParameterAnimCondition")),
            ("m_comparisonOp",Number(0)),("m_paramID",Id(ids[parameter])),
            ("m_comparisonValue",Object(("m_nType",Number(1)),("m_data",Bool(value)))));
        KvObject Finished()=>Object(("_class",Text("CFinishedCondition")),("m_comparisonOp",Number(0)),
            ("m_option",Text("FinishedConditionOption_OnAlmostFinished")),("m_bIsFinished",Bool(true)));
        KvObject Transition(WeaponAction destination,params KvValue[] conditions)=>Object(
            ("_class",Text("CAnimStateTransition")),("m_conditions",Array(conditions)),
            ("m_blendDuration",new KvDouble(.06)),("m_destState",Id(200+(int)destination)),
            ("m_bReset",Bool(true)),("m_resetCycleOption",Text("Beginning")),("m_flFixedCycleValue",new KvDouble(0)),
            ("m_bBlendCycle",Bool(false)),("m_blendCurve",Object(("m_vControlPoint1",Point(.5,0)),("m_vControlPoint2",Point(.5,1)))),
            ("m_bForceFootPlant",Bool(false)),("m_bDisabled",Bool(false)),("m_bRandomTimeBetween",Bool(false)),
            ("m_flRandomTimeStart",new KvDouble(0)),("m_flRandomTimeEnd",new KvDouble(0)));
        var states=new KvArray();
        var initial=actions.ContainsKey(WeaponAction.Deploy)?WeaponAction.Deploy:WeaponAction.Idle;
        foreach(var pair in actions.OrderBy(p=>p.Key))
        {
            var action=pair.Key;var sequenceId=300+(int)action;var transitions=new KvArray();
            if(action==WeaponAction.Holster)
                transitions.Items.Add(Transition(actions.ContainsKey(WeaponAction.Deploy)?WeaponAction.Deploy:WeaponAction.Idle,Condition("b_holster",false)));
            else
            {
                if(actions.ContainsKey(WeaponAction.Holster))transitions.Items.Add(Transition(WeaponAction.Holster,Condition("b_holster")));
                if(action is WeaponAction.Idle or WeaponAction.Fire)
                {
                    if(actions.ContainsKey(WeaponAction.ReloadEmpty))transitions.Items.Add(Transition(WeaponAction.ReloadEmpty,Condition("b_reload"),Condition("b_empty")));
                    if(actions.ContainsKey(WeaponAction.Reload))transitions.Items.Add(actions.ContainsKey(WeaponAction.ReloadEmpty)
                        ?Transition(WeaponAction.Reload,Condition("b_reload"),Condition("b_empty",false)):Transition(WeaponAction.Reload,Condition("b_reload")));
                    if(actions.ContainsKey(WeaponAction.Fire))transitions.Items.Add(Transition(WeaponAction.Fire,Condition("b_attack")));
                    if(actions.ContainsKey(WeaponAction.Deploy))transitions.Items.Add(Transition(WeaponAction.Deploy,Condition("b_deploy")));
                }
                if(action==WeaponAction.Deploy)transitions.Items.Add(Transition(WeaponAction.Idle,Condition("b_deploy_skip")));
                if(action!=WeaponAction.Idle)transitions.Items.Add(Transition(WeaponAction.Idle,Finished()));
            }
            states.Items.Add(Object(("_class",Text("CAnimState")),("m_transitions",transitions),("m_tags",new KvArray()),("m_tagBehaviors",new KvArray()),
                ("m_name",Text(action.ToString())),("m_inputConnection",Connection(sequenceId)),("m_stateID",Id(200+(int)action)),
                ("m_position",Point((int)action*180,0)),("m_bIsStartState",Bool(action==initial)),("m_bIsEndtState",Bool(false)),
                ("m_bIsPassthrough",Bool(false)),("m_bIsRootMotionExclusive",Bool(false)),("m_bAlwaysEvaluate",Bool(false))));
            nodes.Items.Add(Node(sequenceId,Object(("_class",Text("CSequenceAnimNode")),("m_sName",Text(action.ToString())),("m_vecPosition",Point(-450,(int)action*150)),
                ("m_nNodeID",Id(sequenceId)),("m_sNote",Text("Generated weapon and hand animation")),("m_tagSpans",new KvArray()),
                ("m_sequenceName",Text(pair.Value.Sequence)),("m_playbackSpeed",new KvDouble(1)),("m_bLoop",Bool(action==WeaponAction.Idle)))));
        }
        nodes.Items.Add(Node(100,Object(("_class",Text("CStateMachineAnimNode")),("m_sName",Text("Weapon actions")),("m_vecPosition",Point(-100,0)),
            ("m_nNodeID",Id(100)),("m_sNote",Text("Single clock for weapon and custom hands")),("m_states",states),
            ("m_bBlockWaningTags",Bool(false)),("m_bLockStateWhenWaning",Bool(false)))));
        return Kv3.Serialize(doc);
    }
    static KvObject Object(params (string Key,KvValue Value)[] fields){var result=new KvObject();foreach(var (key,value) in fields)result[key]=value;return result;}
    static KvArray Array(IEnumerable<KvValue> items){var result=new KvArray();result.Items.AddRange(items);return result;}
    static KvString Text(string value)=>new(value);
    static KvLong Number(long value)=>new(value);
    static KvBool Bool(bool value)=>new(value);
    static KvObject Id(int value)=>Object(("m_id",Number(value)));
    static KvArray Point(double x,double y)=>Array(new KvValue[]{new KvDouble(x),new KvDouble(y)});
    static KvObject Node(int id,KvObject value)=>Object(("key",Id(id)),("value",value));
    static KvObject Connection(int node)=>Object(("m_nodeID",Id(node)),("m_outputID",Object(("m_id",Number(4294967295)))));
}
