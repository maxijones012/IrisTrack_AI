using System.Windows.Interop;

namespace IrisTrackAI.Services;

public sealed class HotKeyManager : IDisposable
{
    private readonly nint _hwnd;
    private readonly HwndSource _source;
    public event Action<int>? Pressed;
    public HotKeyManager(nint hwnd)
    {
        _hwnd=hwnd; _source=HwndSource.FromHwnd(hwnd); _source.AddHook(WndProc);
        NativeMethods.RegisterHotKey(hwnd, 8, 0, 0x77);
        NativeMethods.RegisterHotKey(hwnd, 9, 0, 0x78);
        NativeMethods.RegisterHotKey(hwnd,10, 0, 0x79);
    }
    private nint WndProc(nint hwnd,int msg,nint wParam,nint lParam,ref bool handled)
    {
        if (msg==0x0312) { Pressed?.Invoke((int)wParam); handled=true; }
        return nint.Zero;
    }
    public void Dispose()
    {
        foreach(var id in new[]{8,9,10}) NativeMethods.UnregisterHotKey(_hwnd,id);
        _source.RemoveHook(WndProc);
    }
}
