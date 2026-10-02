using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

internal static class Program
{
    private const int WH_KEYBOARD_LL = 13;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int VK_LWIN = 0x5B;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_F24 = 0x87;

    private const int CTRL_DELAY_MS = 100;

    private static IntPtr _hook;
    private static LowLevelKeyboardProc _hookProc;

    private static bool _lWinDown;

    /*
     * LCtrlを保留している状態。
     */
    private static bool _ctrlPending;

    /*
     * 保留したCtrlを通常のCtrlとして
     * SendInputで発生させた状態。
     */
    private static bool _ctrlInjected;

    /*
     * タッチパッド由来の特殊シーケンスを
     * 検出した状態。
     */
    private static bool _blockingSequence;

    private static Timer _ctrlTimer;

    private delegate IntPtr LowLevelKeyboardProc(
        int nCode,
        IntPtr wParam,
        IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelKeyboardProc lpfn,
        IntPtr hMod,
        uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(
        IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hhk,
        int nCode,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(
        string lpModuleName);

    [DllImport("user32.dll")]
    private static extern uint SendInput(
        uint nInputs,
        INPUT[] pInputs,
        int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetMessage(
        out MSG lpMsg,
        IntPtr hWnd,
        uint wMsgFilterMin,
        uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(
        ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(
        ref MSG lpMsg);

    private static void Main()
    {
        _hookProc = HookCallback;

        Process process = Process.GetCurrentProcess();
        ProcessModule module = process.MainModule;

        _hook = SetWindowsHookEx(
            WH_KEYBOARD_LL,
            _hookProc,
            GetModuleHandle(module.ModuleName),
            0);

        if (_hook == IntPtr.Zero)
        {
            Console.WriteLine(
                "SetWindowsHookEx failed: " +
                Marshal.GetLastWin32Error());

            Console.ReadLine();
            return;
        }

        Console.WriteLine("TouchpadBlocker started.");
        Console.WriteLine(
            "LCtrl is delayed when LWin is held.");
        Console.WriteLine(
            "LWin + LCtrl + F24 is suppressed.");
        Console.WriteLine();
        Console.WriteLine(
            "Ctrl delay: " + CTRL_DELAY_MS + "ms");
        Console.WriteLine();
        Console.WriteLine("Ctrl+C to exit.");

        Console.CancelKeyPress += OnCancelKeyPress;

        MSG msg;

        while (GetMessage(
            out msg,
            IntPtr.Zero,
            0,
            0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        Cleanup();
    }

    private static void OnCancelKeyPress(
        object sender,
        ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        Cleanup();
        Environment.Exit(0);
    }

    private static void Cleanup()
    {
        if (_ctrlTimer != null)
        {
            _ctrlTimer.Dispose();
            _ctrlTimer = null;
        }

        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private static IntPtr HookCallback(
        int nCode,
        IntPtr wParam,
        IntPtr lParam)
    {
        if (nCode < 0)
        {
            return CallNextHookEx(
                _hook,
                nCode,
                wParam,
                lParam);
        }

        KBDLLHOOKSTRUCT data =
            (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(
                lParam,
                typeof(KBDLLHOOKSTRUCT));

        int vk = (int)data.vkCode;

        bool down =
            wParam == (IntPtr)WM_KEYDOWN ||
            wParam == (IntPtr)WM_SYSKEYDOWN;

        bool up =
            wParam == (IntPtr)WM_KEYUP ||
            wParam == (IntPtr)WM_SYSKEYUP;

        /*
         * LWinの状態だけ記録する。
         *
         * LWin自体は絶対にブロックしない。
         */
        if (vk == VK_LWIN)
        {
            if (down)
                _lWinDown = true;

            if (up)
                _lWinDown = false;

            return CallNextHookEx(
                _hook,
                nCode,
                wParam,
                lParam);
        }

        /*
         * F24が来た。
         *
         * LCtrlを保留中なら、
         * それはタッチパッド由来の
         * LWin + LCtrl + F24 と判断する。
         */
        if (vk == VK_F24 && down)
        {
            if (_ctrlPending && _lWinDown)
            {
                _ctrlPending = false;
                _blockingSequence = true;

                if (_ctrlTimer != null)
                {
                    _ctrlTimer.Dispose();
                    _ctrlTimer = null;
                }

                Console.WriteLine(
                    "[BLOCK] LWin + LCtrl + F24");

                return (IntPtr)1;
            }

            /*
             * 保留中でないF24は普通に通す。
             */
            return CallNextHookEx(
                _hook,
                nCode,
                wParam,
                lParam);
        }

        /*
         * 特殊シーケンス中。
         *
         * F24のupやCtrlのupなどを
         * ゲーム側へ渡さない。
         */
        if (_blockingSequence)
        {
            if (vk == VK_F24 && up)
            {
                _blockingSequence = false;
            }

            return (IntPtr)1;
        }

        /*
         * LCtrl down。
         *
         * LWinが押されている場合だけ保留する。
         *
         * 普通のCtrlは今まで通り即座に通る。
         */
        if (vk == VK_LCONTROL && down)
        {
            if (_lWinDown && !_ctrlPending)
            {
                _ctrlPending = true;

                _ctrlTimer = new Timer(
                    CtrlTimeout,
                    null,
                    CTRL_DELAY_MS,
                    Timeout.Infinite);

                return (IntPtr)1;
            }

            return CallNextHookEx(
                _hook,
                nCode,
                wParam,
                lParam);
        }

        /*
         * 保留中のCtrlがupされた。
         */
        if (vk == VK_LCONTROL && up)
        {
            if (_ctrlPending)
            {
                /*
                 * F24が来ないままCtrlを離した場合。
                 *
                 * 普通のCtrl操作だったので、
                 * down + upを発生させる。
                 */
                _ctrlPending = false;

                if (_ctrlTimer != null)
                {
                    _ctrlTimer.Dispose();
                    _ctrlTimer = null;
                }

                InjectCtrlDown();
                InjectCtrlUp();

                return (IntPtr)1;
            }

            if (_ctrlInjected)
            {
                _ctrlInjected = false;

                InjectCtrlUp();

                return (IntPtr)1;
            }
        }

        return CallNextHookEx(
            _hook,
            nCode,
            wParam,
            lParam);
    }

    /*
     * 100ms以内にF24が来なかった。
     *
     * 普通のCtrl操作だったと判断して
     * Ctrl downを発生させる。
     */
    private static void CtrlTimeout(object state)
    {
        lock (typeof(Program))
        {
            if (!_ctrlPending)
                return;

            _ctrlPending = false;
            _ctrlInjected = true;

            Console.WriteLine(
                "[PASS] LCtrl");

            InjectCtrlDown();
        }
    }

    private static void InjectCtrlDown()
    {
        SendCtrl(false);
    }

    private static void InjectCtrlUp()
    {
        SendCtrl(true);
    }

    private static void SendCtrl(bool keyUp)
    {
        INPUT[] inputs = new INPUT[1];

        inputs[0].type = 1;

        inputs[0].union.ki = new KEYBDINPUT
        {
            wVk = VK_LCONTROL,
            wScan = 0,
            dwFlags = keyUp ? 0x0002u : 0u,
            time = 0,
            dwExtraInfo = IntPtr.Zero
        };

        SendInput(
            1,
            inputs,
            Marshal.SizeOf(typeof(INPUT)));
    }
}