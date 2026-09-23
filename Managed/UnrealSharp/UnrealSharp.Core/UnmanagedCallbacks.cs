using System.Reflection;
using System.Runtime.InteropServices;
using UnrealSharp.Core.Marshallers;

namespace UnrealSharp.Core;

public static class UnmanagedCallbacks
{
    [UnmanagedCallersOnly]
    public static unsafe IntPtr CreateNewManagedObject(IntPtr nativeObject, IntPtr typeHandlePtr, char** error)
    {
        if (!NativeCallbackGate.TryEnter()) return IntPtr.Zero;
        try
        {
        try
        {
            if (nativeObject == IntPtr.Zero)
            {
                throw new ArgumentNullException(nameof(nativeObject));
            }
            
            Type? type = GCHandleUtilities.GetObjectFromHandlePtr<Type>(typeHandlePtr);
            
            if (type == null)
            {
                throw new InvalidOperationException("The provided type handle does not point to a valid type.");
            }

            return UnrealSharpObject.Create(type, nativeObject);
        }
        catch (Exception ex)
        {
#if DN2CPP
            if (ex is TargetInvocationException invocation && invocation.InnerException is not null)
                ex = invocation.InnerException;
#endif
            LogUnrealSharpCore.LogError($"Failed to create new managed object: {ex.Message}");
            *error = (char*)Marshal.StringToHGlobalUni(ex.ToString());
        }

        return IntPtr.Zero;

        }
        finally
        {
            NativeCallbackGate.Exit();
        }
    }

    [UnmanagedCallersOnly]
    public static IntPtr CreateNewManagedObjectWrapper(IntPtr managedObjectHandle, IntPtr typeHandlePtr)
    {
        if (!NativeCallbackGate.TryEnter()) return IntPtr.Zero;
        try
        {
        try
        {
            if (managedObjectHandle == IntPtr.Zero)
            {
                throw new ArgumentNullException(nameof(managedObjectHandle));
            }
            
            Type? type = GCHandleUtilities.GetObjectFromHandlePtr<Type>(typeHandlePtr);
            
            if (type is null)
            {
                throw new InvalidOperationException("The provided type handle does not point to a valid type.");
            }
            
            object? managedObject = GCHandleUtilities.GetObjectFromHandlePtr<object>(managedObjectHandle);
            if (managedObject is null)
            {
                throw new InvalidOperationException("The provided managed object handle does not point to a valid object.");
            }

            MethodInfo? wrapMethod = type.GetMethod("Wrap", BindingFlags.Public | BindingFlags.Static);
            if (wrapMethod is null)
            {
                throw new InvalidOperationException("The provided type does not have a static Wrap method.");
            }
            
            object? createdObject = wrapMethod.Invoke(null, [managedObject]);
            if (createdObject is null)
            {
                throw new InvalidOperationException("The Wrap method did not return a valid object.");
            }

            return GCHandle.ToIntPtr(GCHandleUtilities.AllocateStrongPointer(createdObject, createdObject.GetType().Assembly));
        }
        catch (Exception ex)
        {
            LogUnrealSharpCore.LogError($"Failed to create new managed object: {ex.Message}");
        }

        return IntPtr.Zero;

        }
        finally
        {
            NativeCallbackGate.Exit();
        }
    }
    
    [UnmanagedCallersOnly]
    public static unsafe IntPtr GetManagedMethod(IntPtr typeHandlePtr, char* methodName)
    {
        if (!NativeCallbackGate.TryEnter()) return IntPtr.Zero;
        try
        {
        try
        {
            Type? type = GCHandleUtilities.GetObjectFromHandlePtr<Type>(typeHandlePtr);
            
            if (type == null)
            {
                throw new Exception("Invalid type handle");
            }
            
            string methodNameString = new string(methodName);
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            Type? currentType = type;
            
            while (currentType != null)
            {
                MethodInfo? method = currentType.GetMethod(methodNameString, flags);

                if (method != null)
                {
#if DN2CPP
                    GCHandle methodHandle = GCHandleUtilities.AllocateStrongPointer(method, type.Assembly);
#else
                    IntPtr functionPtr = method.MethodHandle.GetFunctionPointer();
                    GCHandle methodHandle = GCHandleUtilities.AllocateStrongPointer(functionPtr, type.Assembly);
#endif
                    return GCHandle.ToIntPtr(methodHandle);
                }
                
                currentType = currentType.BaseType;
            }

            return IntPtr.Zero;
        }
        catch (Exception e)
        {
            LogUnrealSharpCore.LogError($"Exception while trying to look up managed method: {e.Message}");
        }

        return IntPtr.Zero;

        }
        finally
        {
            NativeCallbackGate.Exit();
        }
    }
    
    [UnmanagedCallersOnly]
    public static void InitializeStruct(IntPtr structHandle, IntPtr buffer)
    {
        if (!NativeCallbackGate.TryEnter()) return;
        try
        {
        try
        {
            Type? structType = GCHandleUtilities.GetObjectFromHandlePtr<Type>(structHandle);
            
            if (structType == null)
            {
                throw new Exception("Invalid struct type handle");
            }
            
            object? structInstance = Activator.CreateInstance(structType);
            
            if (structInstance == null)
            {
                throw new Exception("Failed to create struct instance");
            }

            MethodInfo? methodInfo = structType.GetMethod("ToNative");
            
            if (methodInfo == null)
            {
                throw new Exception("The struct type does not have a ToNative method");
            }
            
            methodInfo.Invoke(structInstance, [buffer]);
        }
        catch (Exception e)
        {
            LogUnrealSharpCore.LogError($"Exception while trying to initialize struct: {e.Message}");
        }

        }
        finally
        {
            NativeCallbackGate.Exit();
        }
    }
    
    [UnmanagedCallersOnly]
    public static unsafe IntPtr GetManagedTypeHandle(IntPtr assemblyHandle, char* fullTypeName)
    {
        if (!NativeCallbackGate.TryEnter()) return IntPtr.Zero;
        try
        {
        try
        {
            string fullTypeNameString = new string(fullTypeName);
            Assembly? loadedAssembly = GCHandleUtilities.GetObjectFromHandlePtrFast<Assembly>(assemblyHandle);

            if (loadedAssembly == null)
            {
                throw new InvalidOperationException("The provided assembly handle does not point to a valid assembly.");
            }

            Type? foundType = loadedAssembly.GetType(fullTypeNameString);

            if (foundType == null)
            {
                return IntPtr.Zero;
            }
            
            return GCHandle.ToIntPtr(GCHandleUtilities.AllocateStrongPointer(foundType, foundType.Assembly));
        }
        catch (TypeLoadException ex)
        {
            LogUnrealSharpCore.LogError($"TypeLoadException while trying to look up managed type: {ex.Message}");
            return IntPtr.Zero;
        }

        }
        finally
        {
            NativeCallbackGate.Exit();
        }
    }
    
    [UnmanagedCallersOnly]
    public static unsafe int InvokeManagedMethod(IntPtr managedObjectHandle,
        IntPtr methodHandlePtr, 
        IntPtr argumentsBuffer, 
        IntPtr returnValueBuffer, 
        IntPtr exceptionTextBuffer)
    {
        if (!NativeCallbackGate.TryEnter()) return 1;
        try
        {
        try
        {
#if DN2CPP
            MethodInfo method = GCHandleUtilities.GetObjectFromHandlePtrFast<MethodInfo>(methodHandlePtr)!;
            object receiver = GCHandleUtilities.GetObjectFromHandlePtrFast<object>(managedObjectHandle)!;
            try
            {
                method.Invoke(method.IsStatic ? null : receiver, new object[] { argumentsBuffer, returnValueBuffer });
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            }
#else
            IntPtr methodHandle = GCHandleUtilities.GetObjectFromHandlePtrFast<IntPtr>(methodHandlePtr)!;
            object managedObject = GCHandleUtilities.GetObjectFromHandlePtrFast<object>(managedObjectHandle)!;
            delegate*<object, IntPtr, IntPtr, void> methodPtr = (delegate*<object, IntPtr, IntPtr, void>) methodHandle;
            methodPtr(managedObject, argumentsBuffer, returnValueBuffer);
#endif
            return 0;
        }
        catch (Exception ex)
        {
            StringMarshaller.ToNative(exceptionTextBuffer, 0, ex.ToString());
            LogUnrealSharpCore.LogError($"Exception during InvokeManagedMethod: {ex.Message}");
            return 1;
        }

        }
        finally
        {
            NativeCallbackGate.Exit();
        }
    }

    [UnmanagedCallersOnly]
    public static void InvokeDelegate(IntPtr delegatePtr)
    {
        if (!NativeCallbackGate.TryEnter()) return;
        try
        {
        try
        {
            Delegate? foundDelegate = GCHandleUtilities.GetObjectFromHandlePtr<Delegate>(delegatePtr);
            
            if (foundDelegate == null)
            {
                throw new Exception("Invalid delegate handle");
            }

#if DN2CPP
            if (foundDelegate is not Action callback)
                throw new NotSupportedException("Native async callbacks must be Action delegates.");
            callback();
#else
            foundDelegate.DynamicInvoke();
#endif
        }
        catch (Exception ex)
        {
            LogUnrealSharpCore.LogError($"Exception during InvokeDelegate: {ex.Message}");
        }

        }
        finally
        {
            NativeCallbackGate.Exit();
        }
    }

    [UnmanagedCallersOnly]
    public static void Dispose(IntPtr handle, IntPtr assemblyHandle)
    {
        if (!NativeCallbackGate.TryEnter()) return;
        try
        {
#if DN2CPP
        try
        {
            if (GCHandleUtilities.GetObjectFromHandlePtr<object>(handle) is IDisposable disposable)
                disposable.Dispose();
        }
        catch (Exception exception)
        {
            LogUnrealSharpCore.LogError(exception.ToString());
        }
        finally
        {
            if (handle != IntPtr.Zero) GCHandleUtilities.Free(GCHandle.FromIntPtr(handle), null);
        }
#else
        GCHandle foundHandle = GCHandle.FromIntPtr(handle);
        
        if (!foundHandle.IsAllocated)
        {
            return;
        }
        
        if (foundHandle.Target is IDisposable disposable)
        {
            disposable.Dispose();
        }

        Assembly? foundAssembly = GCHandleUtilities.GetObjectFromHandlePtr<Assembly>(assemblyHandle);
        GCHandleUtilities.Free(foundHandle, foundAssembly);
#endif

        }
        finally
        {
            NativeCallbackGate.Exit();
        }
    }

    [UnmanagedCallersOnly]
    public static void FreeHandle(IntPtr handle)
    {
        if (!NativeCallbackGate.TryEnter()) return;
        try
        {
#if DN2CPP
        try
        {
            if (GCHandleUtilities.GetObjectFromHandlePtr<object>(handle) is IDisposable disposable)
                disposable.Dispose();
        }
        catch (Exception exception)
        {
            LogUnrealSharpCore.LogError(exception.ToString());
        }
        finally
        {
            if (handle != IntPtr.Zero) GCHandleUtilities.Free(GCHandle.FromIntPtr(handle), null);
        }
#else
        GCHandle foundHandle = GCHandle.FromIntPtr(handle);
        if (!foundHandle.IsAllocated) return;
        
        if (foundHandle.Target is IDisposable disposable)
        {
            disposable.Dispose();
        }
            
        foundHandle.Free();
#endif

        }
        finally
        {
            NativeCallbackGate.Exit();
        }
    }
}