namespace GameManager.Dolphin;

/// <summary>Verbose per-read tracing; enable with GAMEMANAGER_IPC_VERBOSE=1.</summary>
public static class DolphinLog
{
    public static volatile bool Verbose =
        Environment.GetEnvironmentVariable("GAMEMANAGER_IPC_VERBOSE") is "1" or "true" or "TRUE";

    public static void Debug(string message)
    {
        if (Verbose)
            Console.WriteLine(message);
    }
}
