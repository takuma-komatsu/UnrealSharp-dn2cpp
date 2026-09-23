#if DN2CPP
using System.Reflection;
using System.Runtime.InteropServices;
using UnrealSharp.Binds;
using UnrealSharp.Core;

namespace UnrealSharp.Plugins;

public static class Dn2CppBootstrap
{
    private static readonly List<Assembly> Assemblies = new();
    private static readonly List<Assembly> Started = new();
    private static readonly HashSet<Assembly> Loading = new();
    private static bool _initialized;
    private static bool _stopped;

    public static unsafe void Initialize(nint workingDirectory, nint pluginCallbacks, nint bindsCallbacks, nint managedCallbacks)
    {
        if (_initialized || _stopped) throw new InvalidOperationException("Native runtime cannot be initialized twice.");
        try
        {
            NativeBinds.Initialize(bindsCallbacks);
            ManagedCallbacks.Initialize(managedCallbacks);
            PluginsCallbacks.Initialize((PluginsCallbacks*)pluginCallbacks);
            NativeCallbackGate.Open();
            _initialized = true;
        }
        catch
        {
            _stopped = true;
            NativeCallbackGate.Close(0);
            throw;
        }
    }

    public static void PrepareAssembly(Assembly assembly)
    {
        RequireRunning();
        if (Assemblies.Contains(assembly)) throw new InvalidOperationException("Assembly already prepared.");
        GCHandleUtilities.RegisterAssembly(assembly);
        PluginLoader.RegisterStaticAssembly(assembly);
        Assemblies.Add(assembly);
    }

    public static void RegisterAssembly(Assembly assembly)
    {
        RequireRunning();
        if (!Assemblies.Contains(assembly) || !Loading.Add(assembly))
            throw new InvalidOperationException("Assembly is absent or already registered.");
    }

    public static void CompleteAssembly(Assembly assembly)
    {
        RequireRunning();
        if (!Loading.Contains(assembly) || Started.Contains(assembly))
            throw new InvalidOperationException("Assembly has not begun registration or already started.");
        Started.Add(assembly);
        PluginLoader.FindPlugin(assembly)!.StartupModule();
    }

    internal static Assembly FindAssembly(string name)
    {
        RequireRunning();
        foreach (Assembly assembly in Assemblies)
        {
            if (assembly.GetName().Name == name) return assembly;
        }
        throw new NotSupportedException("Assembly is absent from the native registry: " + name);
    }

    public static void Tick(float deltaSeconds)
    {
        RequireRunning();
    }

    public static void Shutdown()
    {
        if (!_initialized || _stopped) return;
        NativeCallbackGate.Close(5000);
        List<Exception> failures = new();
        try
        {
            for (int i = Started.Count - 1; i >= 0; --i)
            {
                try
                {
                    PluginLoader.FindPlugin(Started[i])!.ShutdownModule();
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }
        }
        finally
        {
            _stopped = true;
            Started.Clear();
        }
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    public static void ReleaseHandles()
    {
        if (!_stopped) throw new InvalidOperationException("Stop modules before releasing handles.");
        foreach (Assembly assembly in Assemblies) GCHandleUtilities.FreeAssembly(assembly);
        Assemblies.Clear();
        Loading.Clear();
        PluginLoader.ClearStaticAssemblies();
    }

    private static void RequireRunning()
    {
        if (!_initialized || _stopped) throw new InvalidOperationException("Native runtime is not running.");
    }
}
#endif
