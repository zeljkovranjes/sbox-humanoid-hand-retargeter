#nullable enable
namespace HumanoidHandRetargeter.Target;

/// <summary>Minimal standalone idle/bind graph using the shipped sequence-to-root graph shape.
/// Weapon reload/fire state machines remain owned by the weapon graph.</summary>
public static class HandAnimGraph
{
    public static string Create(string sequenceName, string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sequenceName);
        modelPath = VmdlSetupService.NormalizeAssetPath(modelPath, ".vmdl");
        var doc = Kv3.Parse(Template);
        var root = (KvObject)doc.Root;
        var nodes = (KvArray)((KvObject)root["m_nodeManager"])["m_nodes"];
        var sequence = nodes.Items.Cast<KvObject>().Select(n => (KvObject)n["value"])
            .Single(n => n.GetString("_class") == "CSequenceAnimNode");
        sequence["m_sequenceName"] = new KvString(sequenceName);
        var models = new KvArray();
        models.Items.Add(new KvString(modelPath));
        root["m_previewModels"] = models;
        return Kv3.Serialize(doc);
    }

    // Schema inspected from Facepunch's shipped easter_bonnet_anim.vanmgrph.
    private const string Template = """
<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:animgraph2:version{0f7898b8-5471-45c4-9867-cd9c46bcfdb5} -->
{
	_class = "CAnimationGraph"
	m_nodeManager =
	{
		_class = "CAnimNodeManager"
		m_nodes =
		[
			{
				key =
				{
					m_id = 1120153906
				}
				value =
				{
					_class = "CRootAnimNode"
					m_sName = "Unnamed"
					m_vecPosition = [ 124.0, -136.0 ]
					m_nNodeID =
					{
						m_id = 1120153906
					}
					m_sNote = ""
					m_inputConnection =
					{
						m_nodeID =
						{
							m_id = 1270187183
						}
						m_outputID =
						{
							m_id = 4294967295
						}
					}
				}
			},
			{
				key =
				{
					m_id = 1270187183
				}
				value =
				{
					_class = "CSequenceAnimNode"
					m_sName = "Unnamed"
					m_vecPosition = [ -157.0, -148.0 ]
					m_nNodeID =
					{
						m_id = 1270187183
					}
					m_sNote = ""
					m_tagSpans = [  ]
					m_sequenceName = "bindPose"
					m_playbackSpeed = 1.0
					m_bLoop = true
				}
			},
		]
	}
	m_pParameterList =
	{
		_class = "CAnimParameterList"
		m_Parameters = [  ]
	}
	m_pTagManager =
	{
		_class = "CAnimTagManager"
		m_tags = [  ]
	}
	m_pMovementManager =
	{
		_class = "CAnimMovementManager"
		m_MotorList =
		{
			_class = "CAnimMotorList"
			m_motors = [  ]
		}
		m_MovementSettings =
		{
			_class = "CAnimMovementSettings"
			m_bShouldCalculateSlope = false
		}
	}
	m_pSettingsManager =
	{
		_class = "CAnimGraphSettingsManager"
		m_settingsGroups =
		[
			{
				_class = "CAnimGraphGeneralSettings"
				m_iGridSnap = 16
			},
		]
	}
	m_pActivityValuesList =
	{
		_class = "CActivityValueList"
		m_activities = [  ]
	}
	m_previewModels =
	[
		"models/hands.vmdl",
	]
	m_boneMergeModels = [  ]
	m_cameraSettings =
	{
		m_flFov = 60.0
		m_sLockBoneName = ""
		m_bLockCamera = false
		m_bViewModelCamera = false
	}
}
""";
}
