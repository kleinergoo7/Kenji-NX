// SPDX-License-Identifier: MIT
// Debug-only event naming helper (no behavior). Restored so the Switch 2
// kernel files compile on trees without the nx2 session services.
namespace Ryujinx.HLE.HOS.Services.Am.AppletAE.AllSystemAppletProxiesService.SystemAppletProxy
{
    static class Nx2EventNames
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> Names = new();

        public static void Register(object evt, string name)
        {
            Names[System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(evt)] = name;
        }

        public static string Lookup(object obj)
        {
            return obj != null && Names.TryGetValue(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj), out string name) ? name : null;
        }
    }
}
