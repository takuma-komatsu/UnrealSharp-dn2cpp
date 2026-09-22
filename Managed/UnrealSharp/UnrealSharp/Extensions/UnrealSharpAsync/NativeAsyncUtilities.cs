using System.Runtime.InteropServices;
using UnrealSharp.Core;

namespace UnrealSharp.UnrealSharpAsync;

public static class NativeAsyncUtilities
{
    public static void InitializeAsyncAction(UCSAsyncActionBase action, Action managedCallback)
    {
#if DN2CPP
        GCHandle callbackHandle = GCHandleUtilities.AllocateWeakPointer(managedCallback, action.GetType().Assembly);
#else
        GCHandle callbackHandle = GCHandleUtilities.AllocateWeakPointer(managedCallback);
#endif
        Bind_UCSAsyncBase.CallInitializeAsyncObject(action.NativeObject, GCHandle.ToIntPtr(callbackHandle));
    }
}