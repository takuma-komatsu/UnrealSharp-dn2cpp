using System.Reflection;
using System.Runtime.InteropServices;
using UnrealSharp.Core;
using UnrealSharp.UnrealSharpCore;

namespace UnrealSharp.Plugins;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct PluginsCallbacks
{
    public delegate* unmanaged<char*, NativeBool, IntPtr> LoadPlugin;
    public delegate* unmanaged<char*, void> UnloadPlugin;
    
    [UnmanagedCallersOnly]
    private static nint ManagedLoadPlugin(char* assemblyPath, NativeBool isCollectible)
    {
        if (!NativeCallbackGate.TryEnter()) return IntPtr.Zero;
        try
        {
        try
        {
        Assembly? newPlugin = PluginLoader.LoadPlugin(new string(assemblyPath), isCollectible.ToManagedBool());

        if (newPlugin == null)
        {
            return IntPtr.Zero;
        };

        return GCHandle.ToIntPtr(GCHandleUtilities.AllocateStrongPointer(newPlugin, newPlugin));
        }
        catch (Exception exception)
        {
            LogUnrealSharpPlugins.LogError(exception.ToString());
            return IntPtr.Zero;
        }

        }
        finally
        {
            NativeCallbackGate.Exit();
        }
    }

    [UnmanagedCallersOnly]
    private static void ManagedUnloadPlugin(char* assemblyPath)
    {
        if (!NativeCallbackGate.TryEnter()) return;
        try
        {
#if DN2CPP
        // Static assemblies remain resident until the native host shuts down.
        return;
#else
        PluginLoader.UnloadPlugin(new string(assemblyPath));
#endif

        }
        finally
        {
            NativeCallbackGate.Exit();
        }
    }

    public static void Initialize(PluginsCallbacks* outCallbacks)
    {
        *outCallbacks = new PluginsCallbacks
        {
            LoadPlugin = &ManagedLoadPlugin,
            UnloadPlugin = &ManagedUnloadPlugin,
        };
    }
}