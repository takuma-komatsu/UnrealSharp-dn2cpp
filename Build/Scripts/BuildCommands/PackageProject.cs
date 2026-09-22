using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using AutomationTool;
using UnrealBuildTool;
using UnrealSharp.Automation.Utilities;
using UnrealSharp.Shared;

namespace UnrealSharp.Automation.BuildCommands;

[Help("Packages the UnrealSharp managed code into an Unreal Engine packaged build.")]
[Help("ArchiveDirectory=<Path>", "REQUIRED. The base directory containing the packaged game.")]
[Help("UETargetType=<TargetType>", "REQUIRED. The Unreal Engine target type (Editor, Game, Client, Server).")]
[Help("UEBuildConfig=<Config>", "REQUIRED. The build configuration (Debug, Development, Shipping, Test).")]
[Help("TargetPlatform=<Platform>", "Optional. Target platform. Defaults to Win64.")]
[Help("TargetArchitecture=<Arch>", "Optional. Target architecture. Defaults to X64.")]
[Help("NativeAOT", "Optional flag. Enables Native AOT compilation for the user solution.")]
[Help("UserParams", "Optional. Additional parameters to forward to the user solution build. " + "These should be specified in the format -UserParams=\"-p:Property=Value\" -UserParams=\"--argument\".")]
public class PackageProject : BuildCommand
{
    private const string ManagedFolderName = "Managed";
    private const string BindingsProjectFolder = "UnrealSharp";
    private const int CleanupMaxAttempts = 5;
    private const int CleanupRetryDelayMs = 1000;

    public sealed record PackagingOptions(
        string ArchiveDirectory,
        TargetType TargetType,
        UnrealTargetConfiguration BuildConfiguration,
        UnrealTargetPlatform TargetPlatform,
        UnrealArch TargetArchitecture,
        bool NativeAot,
        string[]? UserParams = null,
        string PackagingBackend = "Clr",
        string? Dn2CppRoot = null);

    public override void ExecuteBuild()
    {
        PackagingOptions Options = ParseOptionsFromCommandLine();
        StartPackaging(Options);
    }

    private void StartPackaging(PackagingOptions options)
    {
        ValidateOptions(options);
        LogOptions(options);

        DotNetSdkUtilities.CopyGlobalJson(this);

        bool native = options.PackagingBackend == "Dn2Cpp";
        string work = Path.Combine(this.GetUnrealSharpIntermediateDirectory(), "Dn2Cpp", options.TargetType.ToString(), options.BuildConfiguration.ToString(), "arm64");
        string PublishFolder = PathUtilities.BuildOutputPath(native ? work : options.ArchiveDirectory);
        CleanBuildArtifacts(PublishFolder);

        string RuntimeIdentifier = DotNetSdkUtilities.GetDotNetRuntimeIdentifier(options.TargetPlatform, options.TargetArchitecture);
        IList<string> Arguments = BuildBaseArguments(RuntimeIdentifier, options, PublishFolder);

        BuildBindingsSolution(Arguments, options.BuildConfiguration, native);
        Arguments.Add($"-p:UnrealSharpManagedReferenceDirectory={PublishFolder}");
        if (native)
        {
            // Global properties are visible before SDK restore paths are evaluated.
            string lane = $"Dn2Cpp/{options.TargetType}/{options.BuildConfiguration}/arm64/";
            Arguments.Add($"-p:BaseOutputPath=bin/{lane}");
            Arguments.Add($"-p:BaseIntermediateOutputPath=obj/{lane}");
        }
        BuildUserBindings(PublishFolder, options, Arguments);
        BuildUserSolution(PublishFolder, Arguments, options.BuildConfiguration, options.UserParams, native ? work : null);
        
        if (native)
        {
            string script = Path.Combine(options.Dn2CppRoot!, "integrations", "unrealsharp", "package-native.py");
            using System.Diagnostics.Process process = new();
            process.StartInfo.FileName = "python3";
            process.StartInfo.UseShellExecute = false;
            foreach (string argument in new[] { script, "--dn2cpp-root", options.Dn2CppRoot!, "--managed", PublishFolder,
                "--archive", options.ArchiveDirectory, "--work", work, "--configuration", options.BuildConfiguration.ToString(),
                "--platform", options.TargetPlatform.ToString(),
                "--unrealsharp-config", Path.Combine(this.GetProjectRootFolder(), "Config", "DefaultUnrealSharp.ini") })
            {
                process.StartInfo.ArgumentList.Add(argument);
            }
            process.Start();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException("dn2cpp native packaging failed.");

        }
        else
        {
            EmitInstalledFlagFile(PublishFolder);
        }

        LoggerUtilities.LogUnrealSharpInfo($"Packaging complete. Published files: {PublishFolder}");
    }

    private PackagingOptions ParseOptionsFromCommandLine()
    {
        string ArchiveDirectory = ParseRequiredStringParam("ArchiveDirectory");
        TargetType TargetType = ParseRequiredEnumParamEnum<TargetType>("UETargetType");
        UnrealTargetConfiguration TargetConfiguration = ParseRequiredEnumParamEnum<UnrealTargetConfiguration>("UEBuildConfig");

        string? PlatformString = ParseOptionalStringParam("TargetPlatform");
        UnrealTargetPlatform TargetPlatform = string.IsNullOrEmpty(PlatformString) ? UnrealTargetPlatform.Win64 : UnrealTargetPlatform.Parse(PlatformString);

        string? ArchString = ParseOptionalStringParam("TargetArchitecture");
        UnrealArch TargetArchitecture = string.IsNullOrEmpty(ArchString)
            ? (TargetPlatform == UnrealTargetPlatform.Android ? UnrealArch.Arm64 : UnrealArch.X64)
            : UnrealArch.Parse(ArchString);

        bool NativeAot = ParseParam("NativeAOT");

        string[] UserParams = ParseParamValues("UserParams");

        return new PackagingOptions(ArchiveDirectory, TargetType, TargetConfiguration, TargetPlatform, TargetArchitecture, NativeAot, UserParams, ParseParamValue("PackagingBackend", TargetType == UnrealBuildTool.TargetType.Editor ? "Clr" : this.PackagingBackend()), ParseOptionalStringParam("Dn2CppRoot") ?? Environment.GetEnvironmentVariable("DN2CPP_ROOT"));
    }

    private static void LogOptions(PackagingOptions options)
    {
        LoggerUtilities.LogUnrealSharpInfo("Packaging project with parameters:");
        LoggerUtilities.LogUnrealSharpInfo($"Archive Directory: {options.ArchiveDirectory}");
        LoggerUtilities.LogUnrealSharpInfo($"Target Platform: {options.TargetPlatform}");
        LoggerUtilities.LogUnrealSharpInfo($"Target Architecture: {options.TargetArchitecture}");
        LoggerUtilities.LogUnrealSharpInfo($"UE Build Configuration: {options.BuildConfiguration}");
        LoggerUtilities.LogUnrealSharpInfo($"UE Target Type: {options.TargetType}");
        LoggerUtilities.LogUnrealSharpInfo($"Native AOT: {options.NativeAot}");

        if (options.UserParams is { Length: > 0 })
        {
            LoggerUtilities.LogUnrealSharpInfo($"User Params: {string.Join(' ', options.UserParams)}");
        }
    }

    private void ValidateOptions(PackagingOptions options)
    {
        if (options.PackagingBackend != "Clr" && options.PackagingBackend != "Dn2Cpp")
            throw new ArgumentException("PackagingBackend must be Clr or Dn2Cpp.");
        if (options.PackagingBackend == "Dn2Cpp")
        {
            if (this.PackagingBackend() != "Dn2Cpp")
                throw new ArgumentException("Set PackagingBackend=Dn2Cpp in DefaultUnrealSharp.ini before cooking; the runtime and package selection must agree.");
            if ((options.TargetPlatform != UnrealTargetPlatform.Mac && options.TargetPlatform != UnrealTargetPlatform.Android) ||
                options.TargetArchitecture != UnrealArch.Arm64 ||
                options.TargetType != TargetType.Game || (options.BuildConfiguration != UnrealTargetConfiguration.Development &&
                options.BuildConfiguration != UnrealTargetConfiguration.Shipping) || options.NativeAot)
                throw new ArgumentException("dn2cpp requires Mac or Android arm64 Game Development or Shipping without NativeAOT.");
            if (string.IsNullOrEmpty(options.Dn2CppRoot) || !File.Exists(Path.Combine(options.Dn2CppRoot, "integrations", "unrealsharp", "package-native.py")))
                throw new ArgumentException("Dn2CppRoot must point to the dn2cpp source checkout.");
            string bindings = PathUtilities.GetUhtGeneratedOutputPath(this.GetUnrealSharpRootFolder(), TargetType.Game);
            if (!Directory.Exists(bindings) || !Directory.EnumerateFiles(bindings, "*.cs", SearchOption.AllDirectories).Any())
                throw new InvalidOperationException("Generate Game bindings with UBT before dn2cpp packaging.");
        }
        ArgumentException.ThrowIfNullOrEmpty(options.ArchiveDirectory);

        if (!Directory.Exists(options.ArchiveDirectory))
        {
            throw new DirectoryNotFoundException($"Archive directory does not exist: {options.ArchiveDirectory}");
        }

        if (options.NativeAot)
        {
            throw new NotSupportedException("Native AOT packaging is not currently supported. This option is reserved for future use and should not be set.");
        }

        string HostFxrPath = DotNetUtilities.LatestHostFxrPath;
        if (!File.Exists(HostFxrPath))
        {
            throw new FileNotFoundException($"Could not locate hostfxr library at expected path: {HostFxrPath}. Ensure that the .NET SDK is installed and accessible.");
        }

        ValidatePlatformArchitecture(options.TargetPlatform, options.TargetArchitecture);
    }

    private static void ValidatePlatformArchitecture(UnrealTargetPlatform platform, UnrealArch architecture)
    {
        if (platform == UnrealTargetPlatform.LinuxArm64 && architecture != UnrealArch.Arm64)
        {
            throw new ArgumentException($"Platform '{platform}' requires architecture '{UnrealArch.Arm64}', " + $"but '{architecture}' was specified.");
        }
    }

    private static void CleanBuildArtifacts(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        LoggerUtilities.LogUnrealSharpInfo($"Cleaning existing output at '{folder}'.");

        for (int Attempt = 1; Attempt <= CleanupMaxAttempts; Attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
                return;
            }
            catch (Exception Ex)
            {
                if (Attempt == CleanupMaxAttempts)
                {
                    throw new IOException($"Failed to clean output directory '{folder}' after {CleanupMaxAttempts} attempts. See inner exception for details.", Ex);
                }

                LoggerUtilities.LogUnrealSharpWarning($"Attempt {Attempt} to clean output directory '{folder}' failed. Retrying... Exception: {Ex.Message}");
                System.Threading.Thread.Sleep(CleanupRetryDelayMs);
            }
        }
    }

    private static IList<string> BuildBaseArguments(string runtimeIdentifier, PackagingOptions options, string publishFolder)
    {
        return
        [
            "--runtime", runtimeIdentifier,

            "-p:UseDefaultOutputPath=true",
            
            $"-p:PublishSelfContained={(options.NativeAot || options.PackagingBackend == "Dn2Cpp" ? "false" : "true")}",

            $"-p:PackagingBackend={options.PackagingBackend}",
            $"-p:UETargetType={options.TargetType}",
            $"-p:UEBuildConfig={options.BuildConfiguration}",

            $"-p:PublishDir=\"{publishFolder}\"",
        ];
    }

    private void BuildBindingsSolution(IList<string> arguments, UnrealTargetConfiguration buildConfig, bool native)
    {
        string BindingsPath = Path.Combine(this.GetUnrealSharpRootFolder(), ManagedFolderName, BindingsProjectFolder);
        if (native)
        {
            BindingsPath = Path.Combine(BindingsPath, "UnrealSharp.Plugins");
        }
        BuildCommands.BuildSolution.RunBuild(BindingsPath, buildConfig, publish: true, arguments);
    }

    private void BuildUserSolution(string publishFolder, IList<string> buildArguments, UnrealTargetConfiguration buildConfig, string[]? userParams, string? nativeWork)
    {
        string ScriptFolder = this.GetProjectScriptFolder();

        IList<string> BuildUserSolutionArguments = buildArguments;
        if (userParams is { Length: > 0 })
        {
            BuildUserSolutionArguments = new List<string>(buildArguments);
            foreach (string UserParam in userParams)
            {
                BuildUserSolutionArguments.Add(UserParam);
            }
        }

        if (nativeWork != null)
        {
            string records = Path.Combine(nativeWork, "UserProjectOutputs");
            CleanBuildArtifacts(records);
            Directory.CreateDirectory(records);
            BuildUserSolutionArguments = new List<string>(BuildUserSolutionArguments)
            {
                $"-p:CustomAfterMicrosoftCommonTargets={Path.Combine(this.GetUnrealSharpRootFolder(), "Build", "Scripts", "Dn2Cpp.ProjectOutputs.targets")}",
                $"-p:Dn2CppProjectOutputRecords={records}"
            };
            BuildCommands.BuildSolution.RunBuild(ScriptFolder, buildConfig, publish: true, BuildUserSolutionArguments);
            PublishMissingRuntimeGlue(publishFolder, BuildUserSolutionArguments, buildConfig);
            string[] assemblies = Directory.GetFiles(records, "*.txt")
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => File.ReadAllLines(path)[0].Trim()).ToArray();
            if (assemblies.Distinct(StringComparer.OrdinalIgnoreCase).Count() != assemblies.Length)
                throw new InvalidOperationException("Multiple built projects publish the same assembly name.");
            if (assemblies.Length == 0)
                throw new InvalidOperationException("Native user build did not record any runtime project outputs.");
            foreach (string assembly in assemblies)
                if (!File.Exists(Path.Combine(publishFolder, assembly)))
                    throw new FileNotFoundException($"Required built project assembly was not published: {assembly}");
            LoadOrderUtilities.TryEmitLoadOrder(assemblies, publishFolder, LoadOrderUtilities.UserLoadOrderName,
                new LoadOrderOptions { Collectible = false, Priority = LoadOrderUtilities.UserLoadOrderPriority });
        }
        else
        {
            BuildCommands.BuildSolution.RunBuild(ScriptFolder, buildConfig, publish: true, BuildUserSolutionArguments);
            PublishMissingRuntimeGlue(publishFolder, BuildUserSolutionArguments, buildConfig);
            EmitUserLoadOrder(publishFolder);
        }
    }

    private void PublishMissingRuntimeGlue(string publishFolder, IList<string> buildArguments, UnrealTargetConfiguration buildConfig)
    {
        foreach (FileInfo project in this.GetManagedProjectFiles()
            .Where(file => file.Name.EndsWith(".RuntimeGlue.csproj", StringComparison.OrdinalIgnoreCase)
                && !ProjectUtilities.IsEditorOnlyProject(file.FullName)))
        {
            string assembly = Path.GetFileNameWithoutExtension(project.Name) + ".dll";
            if (File.Exists(Path.Combine(publishFolder, assembly)))
                continue;

            BuildCommands.BuildSolution.RunBuild(project.DirectoryName!, buildConfig, publish: true, buildArguments);
            if (!File.Exists(Path.Combine(publishFolder, assembly)))
                throw new FileNotFoundException($"Runtime glue project was not published: {project.FullName}");
        }
    }

    private void BuildUserBindings(string publishFolder, PackagingOptions options, IList<string> buildArguments)
    {
        if (this.IsInstalledUnrealSharpBuild())
        {
            CopyInstalledGlue(publishFolder);
            return;
        }

        LoggerUtilities.LogUnrealSharpInfo("Source build detected. Building glue from generated projects and emitting glue load order...");
        BuildUserGlue.Build(this, options.TargetType, options.BuildConfiguration, publishFolder, false, buildArguments);
    }

    private void EmitUserLoadOrder(string publishFolder)
    {
        List<FileInfo> RuntimeProjectFiles = this.GetManagedProjectFiles()
            .Where(file => !ProjectUtilities.IsEditorOnlyProject(file.FullName))
            .ToList();

        if (RuntimeProjectFiles.Count == 0)
        {
            LoggerUtilities.LogUnrealSharpInfo("No runtime projects found. Skipping user load order emission.");
            return;
        }

        LoadOrderOptions Options = new LoadOrderOptions
        {
            Collectible = false,
            Priority = LoadOrderUtilities.UserLoadOrderPriority
        };

        LoadOrderUtilities.TryEmitLoadOrder(RuntimeProjectFiles.Select(file => file.FullName), publishFolder, LoadOrderUtilities.UserLoadOrderName, Options);
    }

    private void CopyInstalledGlue(string publishFolder)
    {
        string GlueFileName = AssemblyUtilities.MakeLoadOrderFileName(LoadOrderUtilities.GlueLoadOrderName);
        string GlueSource = PathUtilities.BuildOutputPath(this.GetProjectRootFolder());
        string GlueManifest = Path.Combine(GlueSource, GlueFileName);

        if (!File.Exists(GlueManifest))
        {
            LoggerUtilities.LogUnrealSharpWarning($"Runtime glue manifest not found at {GlueManifest}. Was the C++ project built at least once? Packaged build may be missing generated glue.");
            return;
        }

        File.Copy(GlueManifest, Path.Combine(publishFolder, GlueFileName), true);

        foreach (string AssemblyName in AssemblyUtilities.ReadLoadOrder(GlueManifest))
        {
            CopyIfExists(Path.Combine(GlueSource, AssemblyName + ".dll"), publishFolder);
            CopyIfExists(Path.Combine(GlueSource, AssemblyName + ".pdb"), publishFolder);
        }
    }

    private static void CopyIfExists(string sourceFile, string destFolder)
    {
        if (!File.Exists(sourceFile))
        {
            return;
        }

        File.Copy(sourceFile, Path.Combine(destFolder, Path.GetFileName(sourceFile)), true);
    }

    private void EmitInstalledFlagFile(string publishFolder)
    {
        string InstalledFlagFilePath = Path.Combine(publishFolder, BuildUtilities.UnrealSharpBuildFlagFileName);
        File.WriteAllText(InstalledFlagFilePath, string.Empty);
    }
}
