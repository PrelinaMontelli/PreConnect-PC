using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PreConnect;

internal sealed class TrayIconHost : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int WM_COMMAND = 0x0111;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int NIF_INFO = 0x00000010;

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;

    private const int IDI_APPLICATION = 32512;
    private const int IMAGE_ICON = 1;
    private const int LR_LOADFROMFILE = 0x0010;
    private const int LR_DEFAULTSIZE = 0x0040;

    private const int GWLP_WNDPROC = -4;

    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint MF_STRING = 0x0000;
    private const uint NIIF_INFO = 0x00000001;

    private const int CommandOpen = 1001;
    private const int CommandExit = 1002;

    private readonly IntPtr _hwnd;
    private readonly Action _openAction;
    private readonly Action _exitAction;
    private readonly uint _callbackMessage;
    private readonly WndProcDelegate _wndProc;
    private IntPtr _originalWndProc;
    private bool _disposed;

    public TrayIconHost(IntPtr hwnd, string tooltip, Action openAction, Action exitAction)
    {
        _hwnd = hwnd;
        _openAction = openAction;
        _exitAction = exitAction;
        _callbackMessage = WM_APP + 200;

        _wndProc = WndProc;
        _originalWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProc));

        var notifyData = CreateNotifyIconData(tooltip);
        Shell_NotifyIcon(NIM_ADD, ref notifyData);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var notifyData = CreateNotifyIconData(string.Empty);
        Shell_NotifyIcon(NIM_DELETE, ref notifyData);

        if (_originalWndProc != IntPtr.Zero)
        {
            SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _originalWndProc);
            _originalWndProc = IntPtr.Zero;
        }
    }

    public void ShowBalloonTip(string title, string text, int timeoutMs = 2500)
    {
        if (_disposed)
        {
            return;
        }

        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_INFO,
            szInfoTitle = title.Length > 63 ? title[..63] : title,
            szInfo = text.Length > 255 ? text[..255] : text,
            uTimeoutOrVersion = (uint)Math.Max(1000, timeoutMs),
            dwInfoFlags = NIIF_INFO
        };

        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private NOTIFYICONDATA CreateNotifyIconData(string tooltip)
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = _callbackMessage,
            hIcon = LoadTrayIconHandle()
        };

        if (!string.IsNullOrWhiteSpace(tooltip))
        {
            data.szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        }

        return data;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _callbackMessage)
        {
            var eventMessage = (int)lParam;
            if (eventMessage == WM_LBUTTONDBLCLK)
            {
                _openAction();
            }
            else if (eventMessage == WM_RBUTTONUP)
            {
                ShowContextMenu();
            }
        }
        else if (msg == WM_COMMAND)
        {
            var command = LowWord(wParam);
            if (command == CommandOpen)
            {
                _openAction();
                return IntPtr.Zero;
            }

            if (command == CommandExit)
            {
                _exitAction();
                return IntPtr.Zero;
            }
        }

        return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            AppendMenu(menu, MF_STRING, CommandOpen, "打开 PreConnect");
            AppendMenu(menu, MF_STRING, CommandExit, "退出");

            GetCursorPos(out var point);
            SetForegroundWindow(_hwnd);
            TrackPopupMenu(menu, TPM_LEFTALIGN | TPM_RIGHTBUTTON, point.X, point.Y, 0, _hwnd, IntPtr.Zero);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static IntPtr LoadTrayIconHandle()
    {
        var baseDir = AppContext.BaseDirectory;
        var iconCandidates = new[]
        {
            Path.Combine(baseDir, "PreConnectApp.ico"),
            Path.Combine(baseDir, "Assets", "PreConnectApp.ico")
        };

        foreach (var iconPath in iconCandidates)
        {
            if (!File.Exists(iconPath))
            {
                continue;
            }

            var icon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
            if (icon != IntPtr.Zero)
            {
                return icon;
            }
        }

        return LoadIcon(IntPtr.Zero, (IntPtr)IDI_APPLICATION);
    }

    private static int LowWord(IntPtr value)
    {
        return unchecked((short)((long)value & 0xFFFF));
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string lpszName, int uType, int cxDesired, int cyDesired, int fuLoad);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, int uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern IntPtr SetWindowLongPtr32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr newProc)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, newProc)
            : SetWindowLongPtr32(hWnd, nIndex, newProc);
    }
}
