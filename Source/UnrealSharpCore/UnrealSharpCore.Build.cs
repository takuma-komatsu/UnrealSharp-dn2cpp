using System.IO;
using UnrealBuildTool;

public class UnrealSharpCore : ModuleRules
{
	public UnrealSharpCore(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
		
		PublicDefinitions.Add("PLUGIN_PATH=" + PluginDirectory.Replace("\\","/"));
		PublicDefinitions.Add("TARGET_TYPE=" + (int)Target.Type);
		PublicDefinitions.Add("TARGET_CONFIGURATION=" + (int)Target.Configuration);
		
		PublicDependencyModuleNames.AddRange(
			new string[]
			{
				"Core", 
				"GameplayTags", 
				"UnrealSharpUtilities",
			}
			);
		
		PrivateDependencyModuleNames.AddRange(
			new string[]
			{
				"CoreUObject",
				"Engine",
				"Slate",
				"SlateCore", 
				"Boost", 
				"Projects",
				"UMG", 
				"DeveloperSettings", 
				"UnrealSharpUtilities", 
				"EnhancedInput", 
				"UnrealSharpUtilities",
				"GameplayTags", 
				"AIModule",
				"UnrealSharpBinds",
				"FieldNotification",
				"InputCore",
				"Json"
			});

        PublicIncludePaths.AddRange(new string[] { ModuleDirectory });
        PublicDefinitions.Add("ForceAsEngineGlue=1");
        PublicSystemIncludePaths.Add(Path.Combine(PluginDirectory, "Managed", "DotNetRuntime", "inc"));

        if (Target.Platform == UnrealTargetPlatform.Android && Target.Type == TargetType.Game && Target.ProjectFile != null)
        {
            string NativeStage = Path.Combine(Target.ProjectFile.Directory.FullName, "Intermediate", "UnrealSharp", "NativeStage", "Android", Target.Configuration.ToString());
            string ManagedStage = Path.Combine(NativeStage, "Binaries", "Managed", "net10.0");
            if (Directory.Exists(ManagedStage))
            {
                foreach (string Manifest in Directory.GetFiles(ManagedStage, "*.LoadOrder.json"))
                {
                    RuntimeDependencies.Add(
                        "$(ProjectDir)/Content/Dn2Cpp/Managed/net10.0/" + Path.GetFileName(Manifest),
                        Manifest, StagedFileType.UFS);
                }
                string Flag = Path.Combine(ManagedStage, "UnrealSharpBuild.flag");
                if (File.Exists(Flag))
                {
                    RuntimeDependencies.Add("$(ProjectDir)/Content/Dn2Cpp/Managed/net10.0/UnrealSharpBuild.flag", Flag, StagedFileType.UFS);
                }
            }
            AdditionalPropertiesForReceipt.Add("AndroidPlugin", Path.Combine(PluginDirectory, "Source", "UnrealSharpCore", "UnrealSharpCore_Android_UPL.xml"));
        }

		if (Target.bBuildEditor)
		{
			PrivateDependencyModuleNames.AddRange(new string[]
			{
				"UnrealEd", 
				"EditorSubsystem",
				"BlueprintGraph",
				"BlueprintEditorLibrary"
			});
		}
	}
}


