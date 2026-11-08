namespace Ryujinx.HLE.HOS.Kernel.Threading
{
    class KEvent
    {
        public KReadableEvent ReadableEvent { get; private set; }
        public KWritableEvent WritableEvent { get; private set; }

        public KEvent(KernelContext context)
        {
            ReadableEvent = new KReadableEvent(context);
            WritableEvent = new KWritableEvent(context, this);

            Ryujinx.Common.Logging.Logger.Info?.Print(Ryujinx.Common.Logging.LogClass.KernelSvc, $"KEvent create hash={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(ReadableEvent)}");

            if (System.Environment.GetEnvironmentVariable("NX2_WAIT_LOG") == "1" ||
                System.Environment.GetEnvironmentVariable("NX2_THREAD_DUMP") == "1" ||
                System.Environment.GetEnvironmentVariable("NX2_HANDLE_OWNER") == "1")
            {
                // Record the creating frame so a wait on this event says who owns it.
                string site = null;

                foreach (string line in System.Environment.StackTrace.Split('\n'))
                {
                    if (line.Contains("Ryujinx.HLE") && !line.Contains("KEvent..ctor") && !line.Contains("KReadableEvent"))
                    {
                        site = line.Trim();

                        if (site.Length > 90)
                        {
                            site = site[^90..];
                        }

                        break;
                    }
                }

                Ryujinx.HLE.HOS.Services.Am.AppletAE.AllSystemAppletProxiesService.SystemAppletProxy.Nx2EventNames.Register(
                    ReadableEvent, site ?? "unknown");
            }
        }
    }
}
