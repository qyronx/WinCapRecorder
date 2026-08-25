using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.Graphics.Capture;
using WinRT;

namespace WinCapRecorder.Capture
{
    internal static partial class WgcInterop
    {
        private static readonly Guid GraphicsCaptureItemIid =
            new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

        // This is the ABI interop interface exposed by the
        // Windows.Graphics.Capture activation factory.
        [GeneratedComInterface]
        [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal partial interface IGraphicsCaptureItemInterop
        {
            IntPtr CreateForWindow(IntPtr window, in Guid iid);
            IntPtr CreateForMonitor(IntPtr monitor, in Guid iid);
        }

        public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                throw new ArgumentException("HWND가 0입니다.", nameof(hwnd));

            try
            {
                // CsWinRT's activation-factory interop is important here.
                // Do not cast the WinRT object with Marshal.GetObjectForIUnknown.
                var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
                IntPtr itemPtr = interop.CreateForWindow(hwnd, in GraphicsCaptureItemIid);

                if (itemPtr == IntPtr.Zero)
                    throw new InvalidOperationException("CreateForWindow가 null 포인터를 반환했습니다.");

                try
                {
                    return GraphicsCaptureItem.FromAbi(itemPtr);
                }
                finally
                {
                    Marshal.Release(itemPtr);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Windows Graphics Capture 항목 생성 실패. 대상 HWND가 유효한지 확인하세요.", ex);
            }
        }

        public static bool IsSupported()
        {
            try { return GraphicsCaptureSession.IsSupported(); }
            catch { return false; }
        }
    }
}
