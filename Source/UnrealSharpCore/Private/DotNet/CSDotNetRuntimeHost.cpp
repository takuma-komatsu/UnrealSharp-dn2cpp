#include "DotNet/CSDotNetRuntimeHost.h"

#include "CSBindsRegistry.h"
#include "CSUnrealSharpSettings.h"
#include "CSManagedCallbacksCache.h"
#include "dn2cpp_unrealsharp_abi.h"
#include "Misc/EngineVersion.h"
#include "Containers/Ticker.h"
#include "UnrealSharpCore.h"
#include "CSDotnetUtilties.h"
#include "CSManagedPluginCallbacks.h"
#include "CSPathsUtilities.h"
#include "HAL/PlatformProcess.h"
#include "Misc/Paths.h"
#include "Logging/StructuredLog.h"
#include <cstdio>

using namespace UnrealSharp;

namespace
{
    decltype(&dn2cpp_unrealsharp_register_assembly) RegisterAssembly = nullptr;
    decltype(&dn2cpp_unrealsharp_tick) TickRuntime = nullptr;
    decltype(&dn2cpp_unrealsharp_shutdown) ShutdownRuntime = nullptr;
    FTSTicker::FDelegateHandle TickHandle;
}

bool FCSDotNetRuntimeHost::UsesDn2Cpp()
{
#if WITH_EDITOR
    return false;
#else
    return GetDefault<UCSUnrealSharpSettings>()->PackagingBackend == ECSPackagingBackend::Dn2Cpp;
#endif
}

bool FCSDotNetRuntimeHost::RegisterNativeAssembly(const FString& Name)
{
    Dn2CppUnrealSharpResult Result{};
    if (!RegisterAssembly || RegisterAssembly(TCHAR_TO_UTF8(*Name), &Result) == 0 || !Result.success)
    {
        const FString ErrorMessage = UTF8_TO_TCHAR(Result.error);
        UE_LOGFMT(LogUnrealSharp, Error, "Native assembly registration failed: {0}", ErrorMessage);
        std::fprintf(stderr, "Native assembly registration failed: %.*s\n", static_cast<int>(sizeof(Result.error)), Result.error);
        std::fflush(stderr);
        return false;
    }
    return true;
}

bool FCSDotNetRuntimeHost::CompleteNativeStartup()
{
    if (!bNativeRuntime)
    {
        return true;
    }
    // The lifecycle export rejects Ready until every compiled assembly registered.
    Dn2CppUnrealSharpResult Result{};
    if (!TickRuntime || TickRuntime(0.0f, &Result) == 0 || !Result.success)
    {
        const FString ErrorMessage = UTF8_TO_TCHAR(Result.error);
        UE_LOGFMT(LogUnrealSharp, Error, "Native startup did not complete its assembly registry: {0}", ErrorMessage);
        std::fprintf(stderr, "Native startup did not complete its assembly registry: %.*s\n", static_cast<int>(sizeof(Result.error)), Result.error);
        std::fflush(stderr);
        return false;
    }
    return true;
}


static_assert(sizeof(DotNetUtilities::FHostChar) == sizeof(char_t), "FHostChar does not match hostfxr's char_t.");

FCSDotNetRuntimeHost::~FCSDotNetRuntimeHost()
{
	ShutdownManagedRuntime();
}

bool FCSDotNetRuntimeHost::InitializeManagedRuntime()
{
    if (UsesDn2Cpp())
    {
        bNativeRuntime = true;
#if PLATFORM_MAC && PLATFORM_CPU_ARM_FAMILY
        const FString Library = FPaths::Combine(FPaths::ProjectDir(), TEXT("Binaries/Mac/libUnrealSharpGame.dylib"));
#elif PLATFORM_ANDROID && PLATFORM_CPU_ARM_FAMILY
        const FString Library = TEXT("libUnrealSharpGame.so");
#endif
#if (PLATFORM_MAC || PLATFORM_ANDROID) && PLATFORM_CPU_ARM_FAMILY
        RuntimeHost = FPlatformProcess::GetDllHandle(*Library);
        decltype(&dn2cpp_unrealsharp_initialize) Initialize = nullptr;
        if (!RuntimeHost || !BindExport(Initialize, TEXT("dn2cpp_unrealsharp_initialize")) ||
            !BindExport(RegisterAssembly, TEXT("dn2cpp_unrealsharp_register_assembly")) ||
            !BindExport(TickRuntime, TEXT("dn2cpp_unrealsharp_tick")) ||
            !BindExport(ShutdownRuntime, TEXT("dn2cpp_unrealsharp_shutdown")))
        {
            UE_LOGFMT(LogUnrealSharp, Error, "Missing dn2cpp runtime or exports: {0}", Library);
            std::fprintf(stderr, "Missing dn2cpp runtime or exports: %.4096s\n", TCHAR_TO_UTF8(*Library));
            std::fflush(stderr);
            return false;
        }
        const FTCHARToUTF8 Directory(*FPaths::ConvertRelativePathToFull(Paths::GetUserAssemblyDirectory()));
        Dn2CppUnrealSharpHost Host{};
        Host.abi_version = DN2CPP_UNREALSHARP_ABI_VERSION;
        Host.struct_size = sizeof(Host);
        Host.ue_major = ENGINE_MAJOR_VERSION;
        Host.ue_minor = ENGINE_MINOR_VERSION;
        Host.ue_patch = ENGINE_PATCH_VERSION;
        Host.unrealsharp_revision = DN2CPP_UNREALSHARP_REVISION;
        Host.working_directory = const_cast<char*>(Directory.Get());
        Host.plugin_callbacks = &GetManagedPluginCallbacks();
        Host.binds_callbacks = reinterpret_cast<void*>(&FCSBindsRegistry::GetBoundFunction);
        Host.managed_callbacks = &GetManagedCallbacks();
        Host.plugin_callbacks_size = sizeof(GetManagedPluginCallbacks());
        Host.managed_callbacks_size = sizeof(GetManagedCallbacks());
        Dn2CppUnrealSharpResult Result{};
        if (Initialize(&Host, &Result) == 0 || !Result.success)
        {
            const FString ErrorMessage = UTF8_TO_TCHAR(Result.error);
            UE_LOGFMT(LogUnrealSharp, Error, "Native initialization failed: {0}", ErrorMessage);
            std::fprintf(stderr, "Native initialization failed: %.*s\n", static_cast<int>(sizeof(Result.error)), Result.error);
            std::fflush(stderr);
            return false;
        }
        TickHandle = FTSTicker::GetCoreTicker().AddTicker(FTickerDelegate::CreateLambda([](float Delta)
        {
            Dn2CppUnrealSharpResult TickResult{};
            if (TickRuntime(Delta, &TickResult) == 0 || !TickResult.success)
            {
                const FString ErrorMessage = UTF8_TO_TCHAR(TickResult.error);
                UE_LOGFMT(LogUnrealSharp, Error, "Native tick failed: {0}", ErrorMessage);
                std::fprintf(stderr, "Native tick failed: %.*s\n", static_cast<int>(sizeof(TickResult.error)), TickResult.error);
                std::fflush(stderr);
                return false;
            }
            return true;
        }));
        return true;
#else
        UE_LOGFMT(LogUnrealSharp, Error, "dn2cpp packaging requires Mac or Android arm64.");
        return false;
#endif
    }
    load_assembly_and_get_function_pointer_fn LoadAssemblyAndGetFunctionPointer = InitializeHost();
	if (!LoadAssemblyAndGetFunctionPointer)
	{
		UE_LOGFMT(LogUnrealSharp, Fatal, "Failed to initialize Runtime Host. Check logs for more details.");
	}

	const FString EntryPointClassName = TEXT("UnrealSharp.Plugins.Main, UnrealSharp.Plugins");
	const FString EntryPointFunctionName = TEXT("InitializeUnrealSharp");
	const FString UnrealSharpLibraryAssembly = FPaths::ConvertRelativePathToFull(Paths::GetUnrealSharpPluginsPath());
	const FString UserWorkingDirectory = FPaths::ConvertRelativePathToFull(Paths::GetUserAssemblyDirectory());

	DotNetUtilities::FHostStringConversion AssemblyPathConv = StringCast<DotNetUtilities::FHostChar>(*UnrealSharpLibraryAssembly);
	DotNetUtilities::FHostStringConversion EntryPointClassConv = StringCast<DotNetUtilities::FHostChar>(*EntryPointClassName);
	DotNetUtilities::FHostStringConversion EntryPointFunctionConv = StringCast<DotNetUtilities::FHostChar>(*EntryPointFunctionName);

	FInitializeUnrealSharp InitializeUnrealSharp = nullptr;
	const int32 ErrorCode = LoadAssemblyAndGetFunctionPointer(
		reinterpret_cast<const char_t*>(AssemblyPathConv.Get()),
		reinterpret_cast<const char_t*>(EntryPointClassConv.Get()),
		reinterpret_cast<const char_t*>(EntryPointFunctionConv.Get()),
		UNMANAGEDCALLERSONLY_METHOD,
		nullptr,
		reinterpret_cast<void**>(&InitializeUnrealSharp));

	if (ErrorCode != 0 || !InitializeUnrealSharp)
	{
		UE_LOGFMT(LogUnrealSharp, Fatal, "Failed to load assembly '{0}'. hostfxr error code: {1}", UnrealSharpLibraryAssembly, ErrorCode);
	}

    const FString ShutdownFunctionName = TEXT("ShutdownUnrealSharp");
    DotNetUtilities::FHostStringConversion ShutdownFunctionConv = StringCast<DotNetUtilities::FHostChar>(*ShutdownFunctionName);
    const int32 ShutdownErrorCode = LoadAssemblyAndGetFunctionPointer(
        reinterpret_cast<const char_t*>(AssemblyPathConv.Get()),
        reinterpret_cast<const char_t*>(EntryPointClassConv.Get()),
        reinterpret_cast<const char_t*>(ShutdownFunctionConv.Get()),
        UNMANAGEDCALLERSONLY_METHOD,
        nullptr,
        reinterpret_cast<void**>(&ShutdownUnrealSharp));
    if (ShutdownErrorCode != 0 || !ShutdownUnrealSharp)
    {
        UE_LOGFMT(LogUnrealSharp, Fatal, "Failed to load shutdown entry point from '{0}'. hostfxr error code: {1}", UnrealSharpLibraryAssembly, ShutdownErrorCode);
    }

	const FTCHARToUTF8 WorkingDirectoryUtf8(*UserWorkingDirectory);

	FCSInitializationResult InitializationResult;

	InitializeUnrealSharp(
		(const UTF8CHAR*)WorkingDirectoryUtf8.Get(),
		&GetManagedPluginCallbacks(),
		(const void*)&FCSBindsRegistry::GetBoundFunction,
		&GetManagedCallbacks(),
		&InitializationResult);

	if (!InitializationResult.bSuccess)
	{
		const FString ErrorMessage = UTF8_TO_TCHAR(InitializationResult.Message);
		UE_LOGFMT(LogUnrealSharp, Fatal, "Failed to initialize UnrealSharp! Exception:\n{0}", ErrorMessage);
	}

#if !(UE_BUILD_SHIPPING)
	if (FParse::Param(FCommandLine::Get(), TEXT("-waitformanageddebugger")))
	{
		while (!FPlatformMisc::IsDebuggerPresent());
	}
#endif

	return true;
}

void FCSDotNetRuntimeHost::ShutdownManagedRuntime()
{
    if (bNativeRuntime)
    {
        FTSTicker::GetCoreTicker().RemoveTicker(TickHandle);
        if (ShutdownRuntime)
        {
            Dn2CppUnrealSharpResult Result{};
            if (ShutdownRuntime(&Result) == 0 || !Result.success)
            {
                const FString ErrorMessage = UTF8_TO_TCHAR(Result.error);
                UE_LOGFMT(LogUnrealSharp, Error, "Native shutdown failed: {0}", ErrorMessage);
                // Shipping may compile out UE logging; shutdown failures must remain observable.
                std::fprintf(stderr, "Native shutdown failed: %.*s\n", static_cast<int>(sizeof(Result.error)), Result.error);
                std::fflush(stderr);
            }
            ShutdownRuntime = nullptr;
        }
        TickRuntime = nullptr;
        RegisterAssembly = nullptr;
        // Managed function pointers remain valid until process exit.
        RuntimeHost = nullptr;
        return;
    }
    if (ShutdownUnrealSharp)
    {
        FCSInitializationResult Result{};
        ShutdownUnrealSharp(&Result);
        if (!Result.bSuccess)
        {
            const FString ErrorMessage = UTF8_TO_TCHAR(Result.Message);
            UE_LOGFMT(LogUnrealSharp, Error, "Managed shutdown failed: {0}", ErrorMessage);
            return;
        }
        ShutdownUnrealSharp = nullptr;
    }
	if (RuntimeHost)
	{
		FPlatformProcess::FreeDllHandle(RuntimeHost);
		RuntimeHost = nullptr;
	}

	Hostfxr_InitForCommandLine = nullptr;
	Hostfxr_InitForRuntimeConfig = nullptr;
	Hostfxr_GetRuntimeDelegate = nullptr;
	Hostfxr_Close = nullptr;
}

FCSDotNetLayout FCSDotNetRuntimeHost::ResolveDotNetLayout(const FString& PluginAssemblyPath)
{
	const FString RuntimeDirectory = FPaths::GetPath(PluginAssemblyPath);

	FCSDotNetLayout Layout;
	Layout.AppAssemblyPath = PluginAssemblyPath;
	Layout.RuntimeConfigPath = DotNetUtilities::GetRuntimeConfigPath(PluginAssemblyPath);

	if (DotNetUtilities::IsSelfContainedDirectory(RuntimeDirectory))
	{
		Layout.DotNetRoot = RuntimeDirectory;
		Layout.HostFxrPath = FPaths::Combine(RuntimeDirectory, DotNetUtilities::GetHostFxrLibraryName());
		Layout.bSelfContained = true;
		return Layout;
	}

	Layout.bSelfContained = false;

	TArray<FString> CandidateRoots;
	CandidateRoots.Add(FPaths::Combine(RuntimeDirectory, TEXT(DOTNET_BUNDLED_FOLDER_NAME)));
	CandidateRoots.Add(FPaths::Combine(RuntimeDirectory, TEXT(".."), TEXT(DOTNET_BUNDLED_FOLDER_NAME)));
	CandidateRoots.Add(FPaths::Combine(FPaths::GetPath(FPlatformProcess::ExecutablePath()), TEXT(DOTNET_BUNDLED_FOLDER_NAME)));
	CandidateRoots.Add(DotNetUtilities::GetDotNetDirectory());

	for (FString& CandidateRoot : CandidateRoots)
	{
		FPaths::CollapseRelativeDirectories(CandidateRoot);

		if (!DotNetUtilities::IsSharedFrameworkRoot(CandidateRoot))
		{
			continue;
		}

		const FString HostFxrPath = DotNetUtilities::GetLatestHostFxrPath(CandidateRoot);
		if (HostFxrPath.IsEmpty())
		{
			continue;
		}

		Layout.DotNetRoot = CandidateRoot;
		Layout.HostFxrPath = HostFxrPath;
		break;
	}

	return Layout;
}

load_assembly_and_get_function_pointer_fn FCSDotNetRuntimeHost::InitializeHost()
{
	const FString PluginAssemblyPath = FPaths::ConvertRelativePathToFull(Paths::GetUnrealSharpPluginsPath());

	const FCSDotNetLayout Layout = ResolveDotNetLayout(PluginAssemblyPath);
	if (!Layout.IsValid())
	{
		UE_LOGFMT(LogUnrealSharp, Error, "Could not resolve a .NET runtime layout for: {0}", PluginAssemblyPath);
		return nullptr;
	}

	UE_LOGFMT(LogUnrealSharp, Log, "AppAssemblyPath: {0}", Layout.AppAssemblyPath);
	UE_LOGFMT(LogUnrealSharp, Log, "RuntimeConfigPath: {0}", Layout.RuntimeConfigPath);
	UE_LOGFMT(LogUnrealSharp, Log, "DotNetRoot: {0}", Layout.DotNetRoot);
	UE_LOGFMT(LogUnrealSharp, Log, "HostFxrPath: {0}", Layout.HostFxrPath);

	RuntimeHost = FPlatformProcess::GetDllHandle(*Layout.HostFxrPath);
	if (!RuntimeHost)
	{
		UE_LOGFMT(LogUnrealSharp, Error, "Failed to get the RuntimeHost DLL handle at: {0}", Layout.HostFxrPath);
		return nullptr;
	}

	const bool BoundAllExports = BindExport(Hostfxr_InitForCommandLine, TEXT("hostfxr_initialize_for_dotnet_command_line"))
		& BindExport(Hostfxr_InitForRuntimeConfig, TEXT("hostfxr_initialize_for_runtime_config"))
		& BindExport(Hostfxr_GetRuntimeDelegate, TEXT("hostfxr_get_runtime_delegate"))
		& BindExport(Hostfxr_Close, TEXT("hostfxr_close"));

	if (!BoundAllExports)
	{
		UE_LOGFMT(LogUnrealSharp, Error, "Failed to resolve all required exports from the Runtime Host.");
		FPlatformProcess::FreeDllHandle(RuntimeHost);
		RuntimeHost = nullptr;
		return nullptr;
	}

	return ConfigureRuntime(Layout);
}

load_assembly_and_get_function_pointer_fn FCSDotNetRuntimeHost::ConfigureRuntime(const FCSDotNetLayout& Layout) const
{
	if (!FPaths::DirectoryExists(Layout.DotNetRoot))
	{
		UE_LOGFMT(LogUnrealSharp, Error, "Dotnet directory does not exist at: {0}", Layout.DotNetRoot);
		return nullptr;
	}

	if (!FPaths::FileExists(Layout.RuntimeConfigPath))
	{
		UE_LOGFMT(LogUnrealSharp, Error, "No runtime config found at: {0}", Layout.RuntimeConfigPath);
		return nullptr;
	}

	UE_LOGFMT(LogUnrealSharp, Log, "Runtime config is self-contained: {0}",
	          DotNetUtilities::IsSelfContainedRuntimeConfig(Layout.RuntimeConfigPath));

	const FString ExecutablePath = FPlatformProcess::ExecutablePath();

	DotNetUtilities::FHostStringConversion DotNetRootConv = StringCast<DotNetUtilities::FHostChar>(*Layout.DotNetRoot);
	DotNetUtilities::FHostStringConversion HostPathConv = StringCast<DotNetUtilities::FHostChar>(*ExecutablePath);

	hostfxr_initialize_parameters InitializeParameters;
	InitializeParameters.size = sizeof(hostfxr_initialize_parameters);
	InitializeParameters.host_path = reinterpret_cast<const char_t*>(HostPathConv.Get());
	InitializeParameters.dotnet_root = reinterpret_cast<const char_t*>(DotNetRootConv.Get());

	hostfxr_handle HostFXR_Handle = nullptr;
	int32 ErrorCode;

	if (Layout.bSelfContained)
	{
		DotNetUtilities::FHostStringConversion AppAssemblyConv = StringCast<DotNetUtilities::FHostChar>(*Layout.AppAssemblyPath);
		const char_t* Args[] = { reinterpret_cast<const char_t*>(AppAssemblyConv.Get()) };
		ErrorCode = Hostfxr_InitForCommandLine(UE_ARRAY_COUNT(Args), Args, &InitializeParameters, &HostFXR_Handle);
	}
	else
	{
		DotNetUtilities::FHostStringConversion RuntimeConfigConv = StringCast<DotNetUtilities::FHostChar>(*Layout.RuntimeConfigPath);
		ErrorCode = Hostfxr_InitForRuntimeConfig(reinterpret_cast<const char_t*>(RuntimeConfigConv.Get()), &InitializeParameters, &HostFXR_Handle);
	}

	if (ErrorCode != 0 || !HostFXR_Handle)
	{
		UE_LOGFMT(LogUnrealSharp, Error, "hostfxr_initialize failed with code: {0}. Set COREHOST_TRACE=1 and COREHOST_TRACEFILE=<path> for the full logging", ErrorCode);
		return nullptr;
	}

	void* LoadAssemblyAndGetFunctionPointer = nullptr;
	ErrorCode = Hostfxr_GetRuntimeDelegate(HostFXR_Handle, hdt_load_assembly_and_get_function_pointer, &LoadAssemblyAndGetFunctionPointer);
	Hostfxr_Close(HostFXR_Handle);

	if (ErrorCode != 0 || !LoadAssemblyAndGetFunctionPointer)
	{
		UE_LOGFMT(LogUnrealSharp, Error, "hostfxr_get_runtime_delegate failed with code: {0}", ErrorCode);
		return nullptr;
	}

	return reinterpret_cast<load_assembly_and_get_function_pointer_fn>(LoadAssemblyAndGetFunctionPointer);
}
