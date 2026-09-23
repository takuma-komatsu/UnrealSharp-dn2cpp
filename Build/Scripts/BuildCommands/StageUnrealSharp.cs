using System;
using System.Collections.Generic;
using System.IO;
using AutomationTool;
using UnrealBuildTool;
using UnrealSharp.Automation.Utilities;

namespace UnrealSharp.Automation.BuildCommands;

[Help("Packages the UnrealSharp managed code for an installed build")] 
[Help("UEBuildConfig=<Config>", "Optional. The build configuration (Debug, Development, Shipping, Test).")]
[Help("UETargetType=<Type>", "Optional. The target type (Editor, Game, etc.). Defaults to Editor.")]
[Help("TargetPlatform=<Platform>", "Optional. Target platform. Defaults to Win64.")]
[Help("TargetArchitecture=<Arch>", "Optional. Target architecture. Defaults to X64.")]
public class StageUnrealSharp : BuildCommand
{
    public override void ExecuteBuild()
    {
        string target = ParseParamValue("UETargetType", nameof(TargetType.Editor));
        string configuration = ParseParamValue("UEBuildConfig", nameof(UnrealTargetConfiguration.Development));
        string backend = target == nameof(TargetType.Editor) ? "Clr" : ParseParamValue("PackagingBackend", this.PackagingBackend());
        string projectRoot = this.GetProjectRootFolder();
        string platform = ParseParamValue("TargetPlatform", string.Empty);
        string nativeArchive = platform == "Android"
            ? Path.Combine(this.GetUnrealSharpIntermediateDirectory(), "NativeStage", "Android", configuration)
            : Path.Combine(this.GetUnrealSharpIntermediateDirectory(), "NativeStage", target, configuration, "arm64", Path.GetFileName(projectRoot));
        string archive = ParseParamValue("ArchiveDirectory", backend == "Dn2Cpp"
            ? nativeArchive
            : projectRoot);
        Directory.CreateDirectory(archive);
        List<KeyValuePair<string, string>> ActionArgs =
        [
            new("ArchiveDirectory", archive),
            new("UEBuildConfig", ParseParamValue("UEBuildConfig", nameof(UnrealTargetConfiguration.Development))),
            new("UETargetType", ParseParamValue("UETargetType", nameof(TargetType.Editor))),
            new("TargetPlatform", platform),
            new("TargetArchitecture", ParseParamValue("TargetArchitecture", platform == "Android" ? "arm64" : string.Empty)),
            new("PackagingBackend", ParseParamValue("UETargetType", nameof(TargetType.Editor)) == nameof(TargetType.Editor) ? "Clr" : ParseParamValue("PackagingBackend", this.PackagingBackend())),
            new("Dn2CppRoot", ParseParamValue("Dn2CppRoot", Environment.GetEnvironmentVariable("DN2CPP_ROOT") ?? string.Empty))
        ];
        
        CommandUtilities.RunCommand(nameof(PackageProject), this, ActionArgs);
    }
}