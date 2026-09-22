#include "UnrealSharpCore.h"
#include "CoreMinimal.h"
#include "CSManager.h"
#include "CSDotnetUtilties.h"
#include "Properties/CSPropertyGeneratorManager.h"
#include "Modules/ModuleManager.h"
#include "Misc/CoreDelegates.h"

#define LOCTEXT_NAMESPACE "FUnrealSharpCoreModule"

DEFINE_LOG_CATEGORY(LogUnrealSharp);

void FUnrealSharpCoreModule::StartupModule()
{
#if WITH_EDITOR
	if (!UnrealSharp::DotNetUtilities::VerifyCSharpEnvironment() || !UnrealSharp::DotNetUtilities::BuildUserSolution())
	{
		return;
	}
#endif
	
	if (!DotNetRuntimeHost.InitializeManagedRuntime())
	{
        if (DotNetRuntimeHost.IsNativeRuntime())
        {
            UE_LOG(LogUnrealSharp, Fatal, TEXT("The selected dn2cpp runtime failed to initialize."));
        }
		return;
	}
	
    FCoreDelegates::OnPreExit.AddRaw(&DotNetRuntimeHost, &FCSDotNetRuntimeHost::ShutdownManagedRuntime);
	UCSManager::Get().Initialize();
    if (!DotNetRuntimeHost.CompleteNativeStartup())
    {
        UE_LOG(LogUnrealSharp, Fatal, TEXT("The dn2cpp assembly registry is incomplete. Check the packaged load-order manifests."));
    }
}

void FUnrealSharpCoreModule::ShutdownModule()
{
    FCoreDelegates::OnPreExit.RemoveAll(&DotNetRuntimeHost);
    DotNetRuntimeHost.ShutdownManagedRuntime();
	FCSPropertyGeneratorManager::Shutdown();
}

#undef LOCTEXT_NAMESPACE
	
IMPLEMENT_MODULE(FUnrealSharpCoreModule, UnrealSharpCore)
