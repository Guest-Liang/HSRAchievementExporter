using System.Runtime.InteropServices;

namespace HSRae.Hook;

public static class HookExports
{
    private static int _started;
    private static Thread? _worker;

    [UnmanagedCallersOnly(EntryPoint = "HSRaeHookMain")]
    public static int Start(nint bootstrapContext)
    {
        _ = bootstrapContext;

        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return 1;
        }

        _worker = new Thread(Run) { IsBackground = true, Name = "HSRae Hook Worker" };
        _worker.Start();
        return 0;
    }

    private static void Run()
    {
        try
        {
            FrameTransport.Connect();
            var installation = PacketHook.WaitForModuleAndInstall(TimeSpan.FromMinutes(2));
            FrameTransport.SendReady(installation.ParserRva);
            FrameTransport.Pump();
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown before GameAssembly.dll was loaded.
        }
        catch (Exception exception)
        {
            FrameTransport.TrySendError(exception.ToString());
        }
        finally
        {
            FrameTransport.RequestShutdown();
            PacketHook.Uninstall();
            FrameTransport.Disconnect();
        }
    }
}
