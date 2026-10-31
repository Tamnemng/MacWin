using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace MacWin.Native
{
    /// <summary>Global hotkeys via RegisterHotKey on a message-only window.</summary>
    internal sealed class HotKeys : IDisposable
    {
        readonly HwndSource _source;
        readonly Dictionary<int, Action> _actions = new();
        int _nextId = 0x4D00;

        public HotKeys()
        {
            var p = new HwndSourceParameters("MacWinHotKeys") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) /*HWND_MESSAGE*/ };
            _source = new HwndSource(p);
            _source.AddHook(WndProc);
        }

        public bool Register(uint mods, uint vk, Action action)
        {
            int id = _nextId++;
            if (!Win32.RegisterHotKey(_source.Handle, id, mods | Win32.MOD_NOREPEAT, vk)) return false;
            _actions[id] = action;
            return true;
        }

        public void Clear()
        {
            foreach (var id in _actions.Keys) Win32.UnregisterHotKey(_source.Handle, id);
            _actions.Clear();
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == Win32.WM_HOTKEY && _actions.TryGetValue(w.ToInt32(), out var a))
            {
                handled = true;
                a();
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            Clear();
            _source.Dispose();
        }
    }

    /// <summary>
    /// Low-level keyboard hook used for "tap the Windows key to open Launchpad".
    /// A Win key press that is released without any other key in between is swallowed
    /// (a harmless unassigned key is injected so the Start menu does not open).
    /// </summary>
    internal sealed class WinKeyTap : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

        delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

        const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;

        [StructLayout(LayoutKind.Sequential)]
        struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public Win32.POINT pt; public uint lPrivate; }
        [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);

        readonly HookProc _proc;
        readonly Action _onTap;
        readonly System.Windows.Threading.Dispatcher _ui;
        IntPtr _hook;
        uint _threadId;
        bool _winDown, _otherPressed;

        /// <summary>
        /// The hook runs on its own thread with its own message loop. Every key typed anywhere in Windows passes
        /// through a low-level hook, so it must never wait for MacWin's UI thread (that caused typing lag).
        /// </summary>
        public WinKeyTap(Action onTap)
        {
            _onTap = onTap;
            _ui = System.Windows.Application.Current.Dispatcher;
            _proc = Callback;
            var ready = new System.Threading.ManualResetEventSlim();
            var t = new System.Threading.Thread(() =>
            {
                _threadId = Win32.GetCurrentThreadId();
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
                ready.Set();
                while (GetMessage(out var m, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref m);
                    DispatchMessage(ref m);
                }
                if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }) { IsBackground = true, Name = "MacWin.KeyHook", Priority = System.Threading.ThreadPriority.AboveNormal };
            t.Start();
            ready.Wait(2000);
        }

        IntPtr Callback(int code, IntPtr w, IntPtr l)
        {
            if (code >= 0)
            {
                var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(l);
                bool injected = (k.flags & 0x10) != 0;
                if (!(injected && k.extra == Input.Signature))
                {
                    int msg = w.ToInt32();
                    bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                    bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                    bool isWin = k.vkCode == Win32.VK_LWIN || k.vkCode == Win32.VK_RWIN;

                    if (isWin && down)
                    {
                        if (!_winDown) { _winDown = true; _otherPressed = false; }
                    }
                    else if (!isWin && down && _winDown)
                    {
                        _otherPressed = true;
                    }
                    else if (isWin && up && _winDown)
                    {
                        _winDown = false;
                        bool mouseUsed = (Win32.GetAsyncKeyState(Win32.VK_LBUTTON) & 0x8000) != 0;
                        if (!_otherPressed && !mouseUsed)
                        {
                            // Swallow this Win release and replay it after a harmless masking key,
                            // so Windows sees "Win, other key, Win up" and keeps the Start menu closed.
                            Input.Chord(Win32.VK_UNASSIGNED);
                            Input.KeyUp((ushort)k.vkCode);
                            _ui.BeginInvoke(_onTap);
                            return new IntPtr(1);
                        }
                    }
                }
            }
            return CallNextHookEx(_hook, code, w, l);
        }

        public void Dispose()
        {
            if (_threadId != 0) PostThreadMessage(_threadId, 0x0012 /*WM_QUIT*/, IntPtr.Zero, IntPtr.Zero);
            _threadId = 0;
        }
    }

    /// <summary>Win32 event hooks (foreground change, window create/destroy, minimise).</summary>
    internal sealed class WinEvents : IDisposable
    {
        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);

        public const uint EVENT_SYSTEM_FOREGROUND = 0x3, EVENT_SYSTEM_MINIMIZESTART = 0x16, EVENT_SYSTEM_MINIMIZEEND = 0x17,
            EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003, EVENT_OBJECT_NAMECHANGE = 0x800C;

        readonly WinEventProc _proc;
        readonly List<IntPtr> _hooks = new();
        public event Action<uint, IntPtr> Event;

        public WinEvents()
        {
            _proc = (h, ev, hwnd, obj, child, t, time) =>
            {
                if (obj != 0 || child != 0) return; // OBJID_WINDOW / CHILDID_SELF only
                Event?.Invoke(ev, hwnd);
            };
            Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND);
            Hook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);
            Hook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE);
            Hook(EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE);
        }

        void Hook(uint min, uint max)
        {
            var h = SetWinEventHook(min, max, IntPtr.Zero, _proc, 0, 0, 0 /*WINEVENT_OUTOFCONTEXT*/ | 2 /*SKIPOWNPROCESS*/);
            if (h != IntPtr.Zero) _hooks.Add(h);
        }

        public void Dispose()
        {
            foreach (var h in _hooks) UnhookWinEvent(h);
            _hooks.Clear();
        }
    }
}
