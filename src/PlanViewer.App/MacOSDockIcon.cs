using System;
using System.Runtime.InteropServices;

namespace PlanViewer.App;

internal static class MacOSDockIcon
{
    private const string ObjCLib = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjCLib, EntryPoint = "objc_getClass")]
    private static extern IntPtr objc_getClass(string className);

    [DllImport(ObjCLib, EntryPoint = "sel_registerName")]
    private static extern IntPtr sel_registerName(string selectorName);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_retIntPtr(IntPtr receiver, IntPtr selector);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_retIntPtr_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);

    public static void SetDockIcon(string iconFilePath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return;

        try
        {
            var nsStringClass = objc_getClass("NSString");
            var allocSel = sel_registerName("alloc");
            var initWithUTF8StringSel = sel_registerName("initWithUTF8String:");

            var nsStringAlloc = objc_msgSend_retIntPtr(nsStringClass, allocSel);
            var pathPtr = Marshal.StringToCoTaskMemUTF8(iconFilePath);
            var nsStringPath = objc_msgSend_retIntPtr_IntPtr(
                nsStringAlloc, initWithUTF8StringSel, pathPtr);
            Marshal.FreeCoTaskMem(pathPtr);

            var nsImageClass = objc_getClass("NSImage");
            var initWithContentsOfFileSel = sel_registerName("initWithContentsOfFile:");

            var nsImageAlloc = objc_msgSend_retIntPtr(nsImageClass, allocSel);
            var nsImage = objc_msgSend_retIntPtr_IntPtr(
                nsImageAlloc, initWithContentsOfFileSel, nsStringPath);

            if (nsImage == IntPtr.Zero)
                return;

            var nsAppClass = objc_getClass("NSApplication");
            var sharedAppSel = sel_registerName("sharedApplication");
            var nsApp = objc_msgSend_retIntPtr(nsAppClass, sharedAppSel);

            var setIconSel = sel_registerName("setApplicationIconImage:");
            objc_msgSend_void_IntPtr(nsApp, setIconSel, nsImage);
        }
        catch
        {
            // Silently fail if native calls fail
        }
    }
}
