# Humanoid Hand Retargeter

Retarget hand and arm animations onto custom FPS rigs in s&box.

* Automatic rig detection and bone mapping
* Profiles for Citizen, Facepunch Human, ActorCore, Mixamo, Unreal, and more
* First person and third person animation previews
* Weapon grip preservation and elbow IK
* Existing weapon AnimGraph support through generated weapon prefabs
* Automatic AnimGraph setup for sources without a graph
* FBX and VMDL animation sources

Add the library to your s&box project and open the **Humanoid Hand Retargeter** editor dock.

1. Select your target hands or arms.
2. Click **Add Files** and choose your source animations or weapon VMDL.
3. Review any flagged bones in **Hand Mapping**.
4. Preview the animation. Select a weapon model to check the grip.
5. Enable **Auto Configure AnimGraph** if you want weapon graph setup.
6. Choose an output path and click **Convert All**.

Generated weapon prefabs that use an existing AnimGraph still require the original weapon assets. When the source weapon or the target uses AnimGraph2, generated graphs are AnimGraph2 (`.ag`, with `.skel` and `.anim` sources); otherwise they are AnimGraph1 (`.vanmgrph`) as before. The dock shows which system the target and each source weapon use. Unrecognized or ambiguous rigs may need manual mapping.

Package ident: `chomnr_humanoid_hand_retargeter`

For editor scripting and LLM automation through sbox-mcp, use `HumanoidHandRetargeter.Editor.HandRetargetApi`. It provides JSON inspection, mapping overrides, dry runs, batch conversion, job status and cancellation. See [llms.txt](llms.txt) for requests, examples and output behavior.
