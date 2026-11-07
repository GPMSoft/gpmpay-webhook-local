using System.Runtime.InteropServices;

namespace GpmPayWebhookLocal.Platform;

/// <summary>
/// macOS: ẩn / hiện biểu tượng trên Dock theo cửa sổ — giống Windows / Ubuntu, khi đóng cửa sổ app chỉ còn
/// biểu tượng trên menu bar.
/// Đổi NSApplication.activationPolicy: Regular (có Dock, có Cmd+Tab) ↔ Accessory (chỉ menu bar).
/// Gọi thẳng Objective-C runtime qua P/Invoke (an toàn với Native AOT); phải gọi trên UI thread (main thread).
/// </summary>
public static class MacDock
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const long PolicyRegular = 0, PolicyAccessory = 1;

    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte SendLong(IntPtr receiver, IntPtr selector, long arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendBool(IntPtr receiver, IntPtr selector, byte arg);

    /// <summary>true: hiện trên Dock và đưa app lên trước; false: chỉ còn biểu tượng trên menu bar.</summary>
    public static void SetVisible(bool visible)
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            if (app == IntPtr.Zero) return;
            SendLong(app, sel_registerName("setActivationPolicy:"), visible ? PolicyRegular : PolicyAccessory);
            // chuyển về Regular xong phải activate thì cửa sổ mới lên trước và menu của app mới hiện
            if (visible) SendBool(app, sel_registerName("activateIgnoringOtherApps:"), 1);
        }
        catch { /* không đổi được thì giữ nguyên Dock, không ảnh hưởng chức năng chính */ }
    }
}
