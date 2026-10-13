using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace BrowserAssistScanner
{
    public sealed class ThumbnailResult : IDisposable
    {
        public Bitmap Image;
        public bool CacheHit;
        public void Dispose() { if (Image != null) { Image.Dispose(); Image = null; } }
    }

    public sealed class ThumbnailService : IDisposable
    {
        private sealed class Request { public string Path; public TaskCompletionSource<ThumbnailResult> Completion = new TaskCompletionSource<ThumbnailResult>(); }
        private sealed class Cached { public string Stamp; public Bitmap Image; public long Used; }
        private readonly object gate = new object();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly Dictionary<string, Cached> cache = new Dictionary<string, Cached>(StringComparer.OrdinalIgnoreCase);
        private readonly Func<string, Bitmap> reader;
        private Request pending;
        private bool disposed;
        private long use;
        public ThumbnailService() : this(ShellThumbnail.Read) { }
        public ThumbnailService(Func<string, Bitmap> reader) {
            this.reader = reader;
            var worker = new Thread(Work) { IsBackground = true, Name = "VAR thumbnail loader" };
            worker.SetApartmentState(ApartmentState.STA); worker.Start();
        }
        public static bool IsVar(string path) { return !String.IsNullOrEmpty(path) && String.Equals(System.IO.Path.GetExtension(path), ".var", StringComparison.OrdinalIgnoreCase); }
        public Task<ThumbnailResult> GetAsync(string path) {
            var request = new Request { Path = path };
            lock (gate) {
                if (disposed) { request.Completion.SetCanceled(); return request.Completion.Task; }
                if (pending != null) pending.Completion.SetCanceled();
                pending = request; wake.Set();
            }
            return request.Completion.Task;
        }
        private void Work() {
            try {
                while (true) {
                    wake.WaitOne(); Request request;
                    lock (gate) { if (disposed) break; request = pending; pending = null; }
                    if (request == null) continue;
                    ThumbnailResult result;
                    try { result = Load(request.Path); } catch { result = new ThumbnailResult(); }
                    lock (gate) {
                        if (disposed) { result.Dispose(); request.Completion.SetCanceled(); }
                        else request.Completion.SetResult(result);
                    }
                }
            } finally {
                foreach (var item in cache.Values) if (item.Image != null) item.Image.Dispose();
                wake.Dispose();
            }
        }
        private ThumbnailResult Load(string path) {
            if (!IsVar(path)) return new ThumbnailResult();
            var file = new FileInfo(path);
            if (!file.Exists) return new ThumbnailResult();
            path = file.FullName;
            string stamp = file.Length + ":" + file.LastWriteTimeUtc.Ticks;
            Cached value;
            bool hit = cache.TryGetValue(path, out value) && value.Stamp == stamp;
            if (!hit) {
                if (value != null && value.Image != null) value.Image.Dispose();
                Bitmap image = null;
                try { image = reader(path); } catch { }
                value = new Cached { Stamp = stamp, Image = image }; cache[path] = value;
            }
            value.Used = ++use;
            if (cache.Count > 64) {
                var oldest = cache.OrderBy(p => p.Value.Used).First();
                if (oldest.Value.Image != null) oldest.Value.Image.Dispose(); cache.Remove(oldest.Key);
            }
            return new ThumbnailResult { Image = value.Image == null ? null : (Bitmap)value.Image.Clone(), CacheHit = hit };
        }
        public void Dispose() {
            lock (gate) {
                if (disposed) return; disposed = true;
                if (pending != null) { pending.Completion.SetCanceled(); pending = null; }
                wake.Set();
            }
        }
    }

    public static class ShellThumbnail
    {
        [StructLayout(LayoutKind.Sequential)] private struct Size2 { public int Width, Height; }
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ImageFactory { [PreserveSig] int GetImage(Size2 size, uint flags, out IntPtr image); }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out ImageFactory factory);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
        public static Bitmap Read(string path) {
            ImageFactory factory = null; IntPtr bitmap = IntPtr.Zero;
            try {
                Guid iid = typeof(ImageFactory).GUID;
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory));
                var size = new Size2 { Width = 256, Height = 256 };
                int hr = factory.GetImage(size, 0x18, out bitmap); // Cached thumbnail only; never substitute a file icon.
                if (hr < 0) {
                    if (bitmap != IntPtr.Zero) { DeleteObject(bitmap); bitmap = IntPtr.Zero; }
                    hr = factory.GetImage(size, 0x08, out bitmap); // Existing Windows thumbnail provider, on the STA worker.
                }
                if (hr < 0 || bitmap == IntPtr.Zero) return null;
                using (var image = Image.FromHbitmap(bitmap)) return new Bitmap(image);
            } finally {
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (factory != null) Marshal.ReleaseComObject(factory);
            }
        }
    }
}
