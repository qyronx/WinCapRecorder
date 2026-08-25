using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace WinCapRecorder.Capture
{
    internal static class D3D11Helper
    {
        private static readonly Guid ID3D11Texture2DIid =
            new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

        private static readonly Guid ID3D11ResourceIid =
            new("DC8E63F3-D12B-4952-B47B-5E45026A862D");

        private static readonly Guid IDirect3DDxgiInterfaceAccessIid =
            new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

        /// <summary>
        /// COM interop for WinRT IDirect3DSurface. Must be [ComImport] so
        /// WinRT.As&lt;T&gt;() can bind to the ABI IInspectable correctly.
        /// </summary>
        [ComImport]
        [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDirect3DDxgiInterfaceAccess
        {
            // HRESULT GetInterface(REFIID iid, void** p) — marshaller throws on failure
            IntPtr GetInterface([In] ref Guid iid);
        }

        [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice",
            ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(
            IntPtr dxgiDevice, out IntPtr graphicsDevice);

        public static ID3D11Device CreateD3DDevice(out IntPtr rawDxgiDevicePtr)
        {
            FeatureLevel[] levels =
            {
                FeatureLevel.Level_11_1,
                FeatureLevel.Level_11_0,
                FeatureLevel.Level_10_1,
                FeatureLevel.Level_10_0
            };

            ID3D11Device? device = null;
            try
            {
                D3D11.D3D11CreateDevice(
                    null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                    levels, out device);
            }
            catch
            {
                D3D11.D3D11CreateDevice(
                    null, DriverType.Warp, DeviceCreationFlags.BgraSupport,
                    levels, out device);
            }

            if (device == null)
                throw new InvalidOperationException("D3D11 장치를 생성하지 못했습니다.");

            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            rawDxgiDevicePtr = dxgiDevice.NativePointer;
            return device;
        }

        public static IDirect3DDevice CreateWinRtDevice(ID3D11Device d3dDevice)
        {
            using var dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>();

            int hr = CreateDirect3D11DeviceFromDXGIDevice(
                dxgiDevice.NativePointer, out IntPtr winrtPtr);

            Marshal.ThrowExceptionForHR(hr);

            if (winrtPtr == IntPtr.Zero)
                throw new InvalidOperationException(
                    "CreateDirect3D11DeviceFromDXGIDevice가 null을 반환했습니다.");

            try
            {
                return MarshalInterface<IDirect3DDevice>.FromAbi(winrtPtr);
            }
            finally
            {
                Marshal.Release(winrtPtr);
            }
        }

        /// <summary>
        /// Returns a native ID3D11Texture2D* from a WGC IDirect3DSurface.
        /// Caller owns the returned reference and must Marshal.Release it.
        /// </summary>
        public static IntPtr GetNativeTexturePointer(IDirect3DSurface surface)
        {
            if (surface == null)
                throw new ArgumentNullException(nameof(surface));

            Exception? last = null;

            // Path A: WinRT.As (CsWinRT-correct)
            try
            {
                var access = surface.As<IDirect3DDxgiInterfaceAccess>();
                Guid iid = ID3D11Texture2DIid;
                IntPtr p = access.GetInterface(ref iid);
                if (p != IntPtr.Zero)
                    return p;
            }
            catch (Exception ex) { last = ex; }

            // Path B: direct cast
            try
            {
                var access = (IDirect3DDxgiInterfaceAccess)(object)surface;
                Guid iid = ID3D11Texture2DIid;
                IntPtr p = access.GetInterface(ref iid);
                if (p != IntPtr.Zero)
                    return p;
            }
            catch (Exception ex) { last = ex; }

            // Path C: IWinRTObject ABI + vtable
            try
            {
                if (surface is IWinRTObject winrtObj)
                {
                    IntPtr abi = winrtObj.NativeObject.ThisPtr;
                    if (abi != IntPtr.Zero)
                    {
                        Guid accessIid = IDirect3DDxgiInterfaceAccessIid;
                        int hr = Marshal.QueryInterface(abi, in accessIid, out IntPtr accessPtr);
                        if (hr >= 0 && accessPtr != IntPtr.Zero)
                        {
                            try
                            {
                                IntPtr vtable = Marshal.ReadIntPtr(accessPtr);
                                IntPtr method = Marshal.ReadIntPtr(vtable, IntPtr.Size * 3);
                                var del = Marshal.GetDelegateForFunctionPointer<GetInterfaceDelegate>(method);
                                Guid texIid = ID3D11Texture2DIid;
                                hr = del(accessPtr, ref texIid, out IntPtr texPtr);
                                if (hr >= 0 && texPtr != IntPtr.Zero)
                                    return texPtr;
                            }
                            finally
                            {
                                Marshal.Release(accessPtr);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { last = ex; }

            throw new InvalidOperationException(
                "WGC surface에서 네이티브 ID3D11Texture2D 포인터를 얻지 못했습니다. " +
                $"detail={last?.GetType().Name}: {last?.Message}",
                last);
        }

        /// <summary>
        /// Wrap a native ID3D11Texture2D* as a Vortice object without re-QueryInterface
        /// through a managed Type.GUID (which is wrong for SharpGen wrappers).
        ///
        /// We use Marshal.GetObjectForIUnknown + force-cast via NativePointer assignment
        /// pattern that works on Vortice 3.x: construct with IntPtr (ownership transfer).
        /// </summary>
        public static ID3D11Texture2D WrapTexture(IntPtr nativeTexture2D)
        {
            if (nativeTexture2D == IntPtr.Zero)
                throw new ArgumentNullException(nameof(nativeTexture2D));

            // Vortice.SharpGen ComObject(IntPtr) stores the pointer as-is and
            // assumes the caller transfers one reference. No Type.GUID QI.
            return new ID3D11Texture2D(nativeTexture2D);
        }

        public static ID3D11Texture2D GetTextureFromSurface(IDirect3DSurface surface)
        {
            IntPtr ptr = GetNativeTexturePointer(surface);
            try
            {
                return WrapTexture(ptr);
            }
            catch
            {
                Marshal.Release(ptr);
                throw;
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetInterfaceDelegate(
            IntPtr self,
            ref Guid iid,
            out IntPtr obj);
    }
}
