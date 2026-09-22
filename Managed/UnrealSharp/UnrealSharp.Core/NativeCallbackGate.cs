namespace UnrealSharp.Core;

public static class NativeCallbackGate
{
    private static int _accepting;
    private static int _active;

    public static void Open()
    {
        if (Interlocked.CompareExchange(ref _accepting, 1, 0) != 0)
            throw new InvalidOperationException("Callbacks are already accepting calls.");
    }

    public static bool TryEnter()
    {
        if (Volatile.Read(ref _accepting) != 1) return false;
        Interlocked.Increment(ref _active);
        if (Volatile.Read(ref _accepting) == 1) return true;
        Interlocked.Decrement(ref _active);
        return false;
    }

    public static void Exit() => Interlocked.Decrement(ref _active);

    public static void Close(int timeoutMilliseconds)
    {
        Volatile.Write(ref _accepting, -1);
        long started = Environment.TickCount64;
        while (Volatile.Read(ref _active) != 0)
        {
            if (Environment.TickCount64 - started >= timeoutMilliseconds)
                throw new TimeoutException("Callbacks did not drain before shutdown.");
            Thread.Sleep(1);
        }
    }
}
