using UnrealBuildTool;

public class UExplorerBridge : ModuleRules
{
	public UExplorerBridge(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = ModuleRules.PCHUsageMode.UseExplicitOrSharedPCHs;

		PublicDependencyModuleNames.AddRange(new string[]
		{
			"Core",
			"CoreUObject",
			"Engine",
			"Json",
			"JsonUtilities"
		});

		PrivateDependencyModuleNames.AddRange(new string[]
		{
			"Slate",
			"SlateCore",
			"UnrealEd",
			"AssetTools",
			"AssetRegistry",
			"MaterialEditor",
			"HTTPServer",
			"ToolMenus",
			"LevelEditor",
			"Projects",
			"Niagara",
			"NiagaraEditor",
			"MeshDescription",
			"StaticMeshDescription"
		});
	}
}
