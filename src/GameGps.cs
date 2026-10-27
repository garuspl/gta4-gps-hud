// Reads the route the game's own GPS has plotted (the line on the minimap) straight from memory.
//
// GTA IV keeps its GPS routes in a small array of slots (CGps-like). For GTAIV.exe 1.2.0.59:
//   slots start at 0x1B332C0 (preferred image base 0xD90000), 2 slots of 0xDB0 bytes each
//   +0x000  route points, 16 bytes each (x, y, z, unused), up to 214 of them
//   +0xD60  int number of points        (this is what the GET_ROUTE_SIZE native returns for slot 1)
//   +0xD64  int state                   (2/3 = route shown, 5 = route shown by the other radar path)
// Found by following GET_ROUTE_SIZE and CRadar's route drawing in the executable, and verified against
// the running game. Before trusting these addresses the reader checks that the exe contains exactly the
// code they came from; on any other game version it reports itself unavailable.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

using GTA;

namespace GPSHud
{
    internal static class GameGps
    {
        const long PreferredBase = 0xD90000;
        const long SlotsVa = 0x1B332C0;
        const int SlotSize = 0xDB0;
        const int SlotCount = 2;
        const int PointStride = 16;
        public const int MaxPoints = 0xD60 / PointStride;
        const int CountOffset = 0xD60;
        const int StateOffset = 0xD64;

        // GET_ROUTE_SIZE's implementation: mov eax, [slot1 + 0xD60]; ret
        const long RouteSizeFuncVa = 0x12C6480;
        const long RouteSizeGlobalVa = 0x1B34DD0;

        static bool checkedVersion;
        static bool available;
        static long delta;
        static readonly float[] buffer = new float[MaxPoints * 4];

        public static string Status = "not checked";

        public static bool Available
        {
            get
            {
                if (!checkedVersion)
                    Check();
                return available;
            }
        }

        static void Check()
        {
            checkedVersion = true;
            try
            {
                long baseAddr = Process.GetCurrentProcess().MainModule.BaseAddress.ToInt64();
                delta = baseAddr - PreferredBase;

                IntPtr func = new IntPtr(RouteSizeFuncVa + delta);
                byte op = Marshal.ReadByte(func);
                int operand = Marshal.ReadInt32(func, 1);
                byte ret = Marshal.ReadByte(func, 5);
                available = op == 0xA1 && operand == (int)(RouteSizeGlobalVa + delta) && ret == 0xC3;
                Status = available
                    ? "game GPS reader active (GTA IV 1.2.0.59 layout)"
                    : "game GPS reader unavailable: unsupported game version";
            }
            catch (Exception ex)
            {
                available = false;
                Status = "game GPS reader unavailable: " + ex.Message;
            }
        }

        // Copies the active GPS route. Returns false if the GPS has no route right now.
        public static bool TryRead(out Vector3[] points, out int slotUsed, out int state)
        {
            points = null;
            slotUsed = -1;
            state = 0;
            if (!Available)
                return false;

            for (int s = 0; s < SlotCount; s++)
            {
                IntPtr slot = new IntPtr(SlotsVa + delta + s * SlotSize);
                int count = Marshal.ReadInt32(slot, CountOffset);
                int st = Marshal.ReadInt32(slot, StateOffset);
                if ((st != 2 && st != 3 && st != 5) || count < 2 || count > MaxPoints)
                    continue;

                Marshal.Copy(slot, buffer, 0, count * 4);
                Vector3[] pts = new Vector3[count];
                bool ok = true;
                for (int i = 0; i < count; i++)
                {
                    float x = buffer[i * 4], y = buffer[i * 4 + 1], z = buffer[i * 4 + 2];
                    // the game may be rewriting the route right now; reject anything that isn't a map position
                    if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z) ||
                        Math.Abs(x) > 6000f || Math.Abs(y) > 6000f || Math.Abs(z) > 2000f)
                    {
                        ok = false;
                        break;
                    }
                    pts[i] = new Vector3(x, y, z);
                }
                if (!ok)
                    continue;

                points = pts;
                slotUsed = s;
                state = st;
                return true;
            }
            return false;
        }
    }
}
