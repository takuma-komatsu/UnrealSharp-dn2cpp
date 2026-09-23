#include "CSManagedCallbacksCache.h"
#include "CSManagedPluginCallbacks.h"

FCSManagedCallbacks& GetManagedCallbacks()
{
    static FCSManagedCallbacks Instance{};
    return Instance;
}

FCSManagedPluginCallbacks& GetManagedPluginCallbacks()
{
    static FCSManagedPluginCallbacks Instance{};
    return Instance;
}
