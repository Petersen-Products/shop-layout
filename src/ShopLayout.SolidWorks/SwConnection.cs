using System;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;

namespace ShopLayout.SolidWorks
{
    /// <summary>Attaches to an already running SolidWorks instance (never starts one).</summary>
    public static class SwConnection
    {
        public const string DefaultProgId = "SldWorks.Application";
        private const int MK_E_UNAVAILABLE = unchecked((int)0x800401E3);

        public static SldWorks Connect(string progId = DefaultProgId)
        {
            object app;
            try
            {
                app = Marshal.GetActiveObject(string.IsNullOrEmpty(progId) ? DefaultProgId : progId);
            }
            catch (COMException ex) when (ex.ErrorCode == MK_E_UNAVAILABLE)
            {
                throw new InvalidOperationException(
                    "No running SolidWorks instance was found. Start SolidWorks and open the shop floor assembly first. " +
                    "(If SolidWorks runs as administrator, this tool must run as administrator too.)", ex);
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException($"Could not attach to SolidWorks ({progId}): {ex.Message}", ex);
            }
            return app as SldWorks ?? throw new InvalidOperationException($"The object registered as {progId} is not a SolidWorks application.");
        }

        /// <summary>Source for the active assembly of a running SolidWorks; hosts need no interop reference.</summary>
        public static SwAssemblySource ConnectToActiveAssembly(string progId, SwSourceOptions options) =>
            new SwAssemblySource(Connect(progId), options);
    }
}
