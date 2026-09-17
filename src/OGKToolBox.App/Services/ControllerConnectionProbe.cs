using System.Runtime.InteropServices;

namespace OGKToolBox.App.Services;

internal static class ControllerConnectionProbe
{
    private const uint JoyReturnAll = 0x000000FF;

    public static bool IsConnected()
    {
        var count = joyGetNumDevs();
        for (uint id = 0; id < count; id++)
        {
            var state = new JoyInfoEx
            {
                Size = (uint)Marshal.SizeOf<JoyInfoEx>(),
                Flags = JoyReturnAll
            };
            if (joyGetPosEx(id, ref state) == 0) return true;
        }
        return false;
    }

    [DllImport("winmm.dll")]
    private static extern uint joyGetNumDevs();

    [DllImport("winmm.dll")]
    private static extern uint joyGetPosEx(uint joystickId, ref JoyInfoEx state);

    [StructLayout(LayoutKind.Sequential)]
    private struct JoyInfoEx
    {
        public uint Size;
        public uint Flags;
        public uint X;
        public uint Y;
        public uint Z;
        public uint R;
        public uint U;
        public uint V;
        public uint Buttons;
        public uint ButtonNumber;
        public uint Pov;
        public uint Reserved1;
        public uint Reserved2;
    }
}

