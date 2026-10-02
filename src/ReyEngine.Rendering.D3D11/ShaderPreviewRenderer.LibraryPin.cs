using System.Runtime.InteropServices;

namespace ReyEngine.Rendering.D3D11;

public sealed unsafe partial class ShaderPreviewRenderer
{
    private static readonly object LibraryPinLock = new();
    private static bool _d3d11Pinned;

    /// <summary>
    /// <para>M809: take one reference on d3d11.dll that nothing in the process ever gives back, so that releasing the LAST device
    /// can never unload it. <see cref="Initialize"/> calls this before the first Silk load, which is why every host of this
    /// renderer is covered: the map viewport, the character window, the Particle Editor, the shader preview and the map
    /// thumbnails.</para>
    ///
    /// <para><see cref="Dispose"/> ends by disposing the Silk API object, and that frees the library handle Silk loaded. When the
    /// device being released was the process's last, d3d11.dll's reference count reached zero and the image was unloaded - while a
    /// thread whose entry point is inside d3d11.dll had been created but had not yet started. It started in code that had just
    /// been unloaded: "Attempt to execute non-executable address" in &lt;Unloaded_d3d11.dll&gt;, a native access violation that no
    /// catch sees and that ends the process. It hit the Map12 thumbnail batches (whose worker owns the only device while the
    /// viewport is idle) and was reproduced two runs out of two under cdb with the debug heap off (M807); it never showed
    /// while another device was alive, because that device's own reference kept the image mapped. M807 pinned the library in
    /// the thumbnail renderer alone; this is the same pin where every device is created.</para>
    ///
    /// <para>Only the library IMAGE stays mapped. The device, its contexts and everything on the GPU are released exactly as
    /// before, and the image is the one the viewport maps for as long as the editor runs anyway. Safe to call from any
    /// thread, any number of times; later calls return at once.</para>
    /// </summary>
    public static void PinD3D11Library()
    {
        lock (LibraryPinLock)
        {
            if (_d3d11Pinned) return;
            // The handle is dropped on purpose: no code path frees this reference. A second caller blocks on the lock until the
            // load has returned, so nobody can create a device (and later release it) ahead of the pin.
            try { NativeLibrary.TryLoad("d3d11.dll", out _); }
            catch { /* not Windows, or no such library: there is no device to release either */ }
            _d3d11Pinned = true;
        }
    }
}
