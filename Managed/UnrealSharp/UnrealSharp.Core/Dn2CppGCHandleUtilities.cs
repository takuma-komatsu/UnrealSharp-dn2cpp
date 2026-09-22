#if DN2CPP
using System.Reflection;
using System.Runtime.InteropServices;

namespace UnrealSharp.Core;

public static class GCHandleUtilities
{
    private static readonly object Sync = new();
    private static readonly Dictionary<Assembly, Dictionary<nint, object?>> Owners = new();
    private static readonly HashSet<Assembly> Closed = new();

    public static void RegisterAssembly(Assembly assembly)
    {
        lock (Sync)
        {
            if (Closed.Contains(assembly)) throw new InvalidOperationException("Assembly owner has shut down.");
            if (!Owners.ContainsKey(assembly)) Owners.Add(assembly, new());
        }
    }

    private static GCHandle Allocate(object value, Assembly owner, bool strong)
    {
        lock (Sync)
        {
            if (!Owners.TryGetValue(owner, out var handles))
                throw new InvalidOperationException("Assembly owner is not registered.");
            GCHandle handle = GCHandle.Alloc(value, GCHandleType.Weak);
            handles.Add(GCHandle.ToIntPtr(handle), strong ? value : null);
            return handle;
        }
    }

    public static GCHandle AllocateStrongPointer(object value, Assembly owner) => Allocate(value, owner, true);
    public static GCHandle AllocateWeakPointer(object value) => Allocate(value, value.GetType().Assembly, false);
    public static GCHandle AllocateWeakPointer(object value, Assembly owner) => Allocate(value, owner, false);

    public static void Free(GCHandle handle, Assembly? assembly)
    {
        lock (Sync)
        {
            foreach (var handles in Owners.Values)
            {
                if (handles.Remove(GCHandle.ToIntPtr(handle)))
                {
                    handle.Free();
                    return;
                }
            }
        }
    }

    public static void FreeAssembly(Assembly assembly)
    {
        lock (Sync)
        {
            Closed.Add(assembly);
            if (Owners.Remove(assembly, out var handles))
            {
                foreach (nint pointer in handles.Keys) GCHandle.FromIntPtr(pointer).Free();
            }
        }
    }

    public static T? GetObjectFromHandlePtr<T>(nint pointer)
    {
        if (pointer == 0) return default;
        lock (Sync)
        {
            GCHandle handle = GCHandle.FromIntPtr(pointer);
            foreach (var handles in Owners.Values)
            {
                if (handles.ContainsKey(pointer)) return handle.Target is T value ? value : default;
            }
            return default;
        }
    }

    public static T? GetObjectFromHandlePtrFast<T>(nint handle) => GetObjectFromHandlePtr<T>(handle);
}
#endif
