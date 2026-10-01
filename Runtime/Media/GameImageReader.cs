using System;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Game.Media
{
    public static class GameImageReader
    {
        internal static long EstimatedInFlightDecodeBytes {get;private set;}
        static readonly SemaphoreSlim processingGate = new SemaphoreSlim(1, 1);

        public static UniTask<ImageTexture> LoadThumbnailAsync(ImageReference image, int maxEdge = 256, CancellationToken cancellationToken = default)
            => LoadPreviewAsync(image, new ImagePreviewOptions { MaxEdge = maxEdge }, cancellationToken);

        public static async UniTask<ImageTexture> LoadPreviewAsync(ImageReference image, ImagePreviewOptions options = null, CancellationToken cancellationToken = default)
        {
            MediaThread.Check(); if (image == null) throw new ArgumentNullException(nameof(image));
            options ??= new ImagePreviewOptions(); options.Validate(); cancellationToken.ThrowIfCancellationRequested();
            using var admission=ImageWorkBudget.Acquire(image,true,true);
            int edge = options.MaxEdge, pixels = options.MaxPixels; bool readable = options.Readable;
            IDisposable lease=null;ImageTexture result=null;bool entered=false;var cleanup=new UIFrame.CleanupFailure();
            try
            {
                lease=image.Acquire();
                await processingGate.WaitAsync(cancellationToken);entered=true;
                result=await LoadPreviewCore(image,edge,pixels,readable,cancellationToken);
            }
            catch(Exception error){cleanup.Capture(error);}
            finally
            {
                if(entered)try{await ReleaseProcessing();}catch(Exception error){cleanup.Capture(error);}
                if(lease!=null)cleanup.Run(lease.Dispose);
            }
            return Deliver(result,ref cleanup);
        }

        internal static async UniTask<ImageTexture> LoadAdmittedPreviewAsync(ImageReference image,ImagePreviewOptions options,CancellationToken token)
        {
            ImageTexture result=null;bool entered=false;var cleanup=new UIFrame.CleanupFailure();
            try
            {
                await processingGate.WaitAsync(token);entered=true;
                result=await LoadPreviewCore(image,options.MaxEdge,options.MaxPixels,options.Readable,token);
            }
            catch(Exception error){cleanup.Capture(error);}
            finally {if(entered)try{await ReleaseProcessing();}catch(Exception error){cleanup.Capture(error);}}
            return Deliver(result,ref cleanup);
        }

        // The operation owns its result until all required source/decoder
        // cleanup succeeds. A failed handoff must not orphan that result.
        static T Deliver<T>(T result,ref UIFrame.CleanupFailure cleanup) where T:class,IDisposable
        {
            try {cleanup.Throw();return result;}
            catch
            {
                if(result!=null)cleanup.Run(result.Dispose);
                throw;
            }
        }

        static async UniTask ReleaseProcessing()
        {
            // Runtime Destroy is deferred; do not admit another decoder in the same frame.
            try { if (Application.isPlaying) await UniTask.NextFrame(); }
            finally { processingGate.Release(); }
        }

        static async UniTask<ImageTexture> LoadPreviewCore(ImageReference image, int edge, int pixels, bool readable, CancellationToken cancellationToken)
        {
            string nativeDirectory = null; string path = image.Id; Texture2D texture = null;
            bool failed = false;
            EstimatedInFlightDecodeBytes=checked((long)Math.Min((long)edge*edge,pixels)*16);
            try
            {
                if (NativeMedia.Available)
                {
                    string output = ImagePaths.NewDirectory();
                    var result = await NativeMedia.Request(new MediaRequest { op = "preview", source = image.Source, path = image.Id, output = output, edge = edge, maxPixels = pixels }, cancellationToken);
                    nativeDirectory = output; path = result.items[0].path;
                }
                else if (image.Source != "file") throw new PlatformNotSupportedException("Native source unavailable.");
                var header = await UniTask.RunOnThreadPool(() => ImageHeader.ReadFile(path));
                if (!NativeMedia.Available && (long)header.Width * header.Height > 16 * 1024 * 1024)
                    throw new GalleryException("ImageTooLarge", "Desktop preview is limited to 16 megapixels; mobile uses native downsampling.");
                ImageHeader.CheckTarget(header.Width, header.Height, edge, pixels);
                EstimatedInFlightDecodeBytes=checked((long)header.Width*header.Height*8+(long)Math.Min((long)edge*edge,pixels)*12);
                bool transform = header.Orientation > 1 || Math.Max(header.Width, header.Height) > edge;
                var parameters = DownloadedTextureParams.Default;
                parameters.mipmapChain = false; parameters.readable = readable && !transform;
                cancellationToken.ThrowIfCancellationRequested();
                using (var request = UnityWebRequestTexture.GetTexture(new Uri(path).AbsoluteUri, parameters))
                {
                    // Complete this bounded local decode and take ownership before observing cancellation.
                    // Canceling the await can orphan a texture already created by the download handler.
                    await request.SendWebRequest().ToUniTask();
                    texture = DownloadHandlerTexture.GetContent(request);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (transform)
                {
                    var transformed = RenderImage(texture, header.Orientation, edge, false, default);
                    ImageTexture.Destroy(texture); texture = transformed;
                }
                if (!readable && texture.isReadable) texture.Apply(false, true);
                cancellationToken.ThrowIfCancellationRequested();
                if (nativeDirectory != null) { Directory.Delete(nativeDirectory, true); nativeDirectory = null; }
                var resource = new ImageTexture(texture); texture = null; return resource;
            }
            catch { failed = true; throw; }
            finally
            {
                EstimatedInFlightDecodeBytes=0;
                ImageTexture.Destroy(texture);
                if (nativeDirectory != null && failed) ImagePaths.CleanAfterFailure(nativeDirectory);
            }
        }

        public static async UniTask<ImageFile> ExportFileAsync(ImageReference image, ImageExportOptions options = null, CancellationToken cancellationToken = default)
        {
            MediaThread.Check(); if (image == null) throw new ArgumentNullException(nameof(image));
            options ??= new ImageExportOptions(); options.Validate(); cancellationToken.ThrowIfCancellationRequested();
            using var admission=ImageWorkBudget.Acquire(image,true,true);
            var copy = new ImageExportOptions { Mode = options.Mode, MaxEdge = options.MaxEdge, MaxPixels = options.MaxPixels,
                JpegQuality = options.JpegQuality, JpegBackground = options.JpegBackground };
            IDisposable lease=null;ImageFile result=null;bool entered=false;var cleanup=new UIFrame.CleanupFailure();
            try
            {
                lease=image.Acquire();
                if(copy.Mode!=ImageExportMode.PreserveProvidedBytes)
                {await processingGate.WaitAsync(cancellationToken);entered=true;}
                result=await ExportCore(image,copy,cancellationToken);
            }
            catch(Exception error){cleanup.Capture(error);}
            finally
            {
                if(entered)try{await ReleaseProcessing();}catch(Exception error){cleanup.Capture(error);}
                if(lease!=null)cleanup.Run(lease.Dispose);
            }
            return Deliver(result,ref cleanup);
        }

        static async UniTask<ImageFile> ExportCore(ImageReference image, ImageExportOptions options, CancellationToken cancellationToken)
        {
            if (NativeMedia.Available && (options.Mode != ImageExportMode.PreserveProvidedBytes || image.Source != "file"))
            {
                var folder = ImagePaths.NewDirectory();
                var result = await NativeMedia.Request(new MediaRequest { op = options.Mode == ImageExportMode.PreserveProvidedBytes ? "export" : "preview",
                    source = image.Source, path = image.Id, output = folder, edge = options.MaxEdge, maxPixels = options.MaxPixels, quality = options.JpegQuality,
                    format = options.Mode == ImageExportMode.Jpeg ? "jpg" : "png", backgroundR = options.JpegBackground.r,
                    backgroundG = options.JpegBackground.g, backgroundB = options.JpegBackground.b }, cancellationToken);
                try { return new ImageFile(result.items[0].path, new ImageStorage(folder)); }
                catch { ImagePaths.CleanAfterFailure(folder); throw; }
            }
            string directory = ImagePaths.NewDirectory();
            try
            {
                string path;
                if (options.Mode == ImageExportMode.PreserveProvidedBytes)
                {
                    path = Path.Combine(directory, "image" + ImagePaths.Extension(image.FileName));
                    await UniTask.RunOnThreadPool(() => GameGallery.CopyFile(image.Id, path, cancellationToken));
                }
                else
                {
                    using var preview = await LoadPreviewCore(image, options.MaxEdge, options.MaxPixels, options.Mode == ImageExportMode.Png, cancellationToken);
                    byte[] bytes;
                    if (options.Mode == ImageExportMode.Jpeg)
                    {
                        var flattened = RenderImage(preview.Texture, 1, options.MaxEdge, true, options.JpegBackground);
                        try { bytes = ImageConversion.EncodeToJPG(flattened, options.JpegQuality); }
                        finally { ImageTexture.Destroy(flattened); }
                        path = Path.Combine(directory, "image.jpg");
                    }
                    else { bytes = ImageConversion.EncodeToPNG(preview.Texture); path = Path.Combine(directory, "image.png"); }
                    await UniTask.RunOnThreadPool(() => File.WriteAllBytes(path, bytes));
                }
                cancellationToken.ThrowIfCancellationRequested();
                return new ImageFile(path, new ImageStorage(directory));
            }
            catch { ImagePaths.CleanAfterFailure(directory); throw; }
        }

        static Shader transformShader;
        static Texture2D RenderImage(Texture2D source, int orientation, int edge, bool composite, Color background)
        {
            int w = orientation >= 5 ? source.height : source.width, h = orientation >= 5 ? source.width : source.height;
            float scale = Math.Min(1f, edge / (float)Math.Max(w, h));
            int width = Math.Max(1, (int)(w * scale)), height = Math.Max(1, (int)(h * scale));
            if (transformShader == null) transformShader = Resources.Load<Shader>("UIFrameImageTransform");
            if (transformShader == null || !transformShader.isSupported) throw new PlatformNotSupportedException("UIFrame image transform shader unavailable.");
            var previous = RenderTexture.active;
            var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            Material material = null; Texture2D result = null;
            try
            {
                material = new Material(transformShader);
                material.SetInt("_Orientation", orientation); material.SetInt("_Composite", composite ? 1 : 0);
                material.SetVector("_Background", new Vector4(background.r, background.g, background.b, 1));
                Graphics.Blit(source, target, material); RenderTexture.active = target;
                result = new Texture2D(width, height, TextureFormat.RGBA32, false);
                result.ReadPixels(new Rect(0, 0, width, height), 0, 0); result.Apply(false); return result;
            }
            catch { ImageTexture.Destroy(result); throw; }
            finally { ImageTexture.Destroy(material); RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); }
        }
    }

    internal readonly struct ImageHeader
    {
        public readonly int Width, Height, Orientation;
        ImageHeader(int width, int height, int orientation) { Width = width; Height = height; Orientation = orientation; }
        internal static void CheckTarget(int width, int height, int edge, int pixels)
        {
            double scale = Math.Min(1d, edge / (double)Math.Max(width, height));
            long w = Math.Max(1, (long)(width * scale)), h = Math.Max(1, (long)(height * scale));
            if (w * h > pixels) throw new GalleryException("ImageTooLarge", "Requested image exceeds MaxPixels; reduce MaxEdge or explicitly raise the pixel budget.");
        }
        internal static ImageHeader ReadFile(string path)
        {
            using var input = File.OpenRead(path);
            if (input.Length > 128L * 1024 * 1024)
                throw new GalleryException("ImageTooLarge", "Preview encoded input is limited to 128 MiB; use file export for larger originals.");
            return ReadStream(input);
        }
        static ImageHeader ReadStream(Stream input)
        {
            int first = input.ReadByte(), second = input.ReadByte();
            if (first == 137 && second == 80)
            {
                input.Position = 0; var png = new byte[24]; ReadExact(input, png, 0, png.Length);
                if (png[2] != 78 || png[3] != 71) throw new GalleryException("InvalidImage", "Invalid PNG signature.");
                int width = Big(png, 16, 4), height = Big(png, 20, 4);
                if (width <= 0 || height <= 0) throw new GalleryException("InvalidImage", "Invalid PNG dimensions.");
                return new ImageHeader(width, height, 1);
            }
            if (first != 255 || second != 216) throw new GalleryException("UnsupportedFormat", "Managed preview supports JPEG and PNG.");
            int w = 0, h = 0, orientation = 1;
            // A single reusable JPEG segment: memory does not grow with APP1/XMP segment count.
            var segment = new byte[65535];
            while (input.Position < input.Length)
            {
                int prefix = input.ReadByte(), marker = input.ReadByte();
                if (prefix != 255 || marker < 0) break;
                while (marker == 255) marker = input.ReadByte();
                if (marker < 0 || marker == 217 || marker == 218) break;
                if (marker == 1 || marker >= 208 && marker <= 215) continue;
                int high = input.ReadByte(), low = input.ReadByte();
                if (high < 0 || low < 0) break;
                int length = (high << 8) | low;
                if (length < 2 || input.Position + length - 2 > input.Length) throw new GalleryException("InvalidImage", "Truncated JPEG segment.");
                int count = length - 2;
                if (marker >= 192 && marker <= 195 && count >= 6)
                {
                    ReadExact(input, segment, 0, 6); h = Big(segment, 1, 2); w = Big(segment, 3, 2);
                    input.Seek(count - 6, SeekOrigin.Current);
                }
                else if (marker == 225 && count >= 14)
                {
                    ReadExact(input, segment, 0, 6);
                    if (segment[0] == 69 && segment[1] == 120 && segment[2] == 105 && segment[3] == 102 && segment[4] == 0 && segment[5] == 0)
                    {
                        ReadExact(input, segment, 6, count - 6);
                        orientation = ReadOrientation(segment, count);
                    }
                    else input.Seek(count - 6, SeekOrigin.Current);
                }
                else input.Seek(count, SeekOrigin.Current);
            }
            if (w <= 0 || h <= 0) throw new GalleryException("InvalidImage", "JPEG dimensions missing.");
            return new ImageHeader(w, h, orientation >= 1 && orientation <= 8 ? orientation : 1);
        }
        static void ReadExact(Stream input, byte[] bytes, int offset, int count)
        {
            while (count > 0) { int n = input.Read(bytes, offset, count); if (n == 0) throw new GalleryException("InvalidImage", "Truncated image header."); offset += n; count -= n; }
        }
        static int ReadOrientation(byte[] bytes, int end)
        {
            const int start = 6; bool little = bytes[start] == 73;
            uint Read(int offset, int size)
            {
                if (offset < start || offset > end - size) throw new GalleryException("InvalidImage", "Invalid EXIF offset.");
                uint value = 0; for (int i = 0; i < size; i++) value = (value << 8) | bytes[offset + (little ? size - 1 - i : i)]; return value;
            }
            uint offset = Read(start + 4, 4);
            if (offset > end - start - 2) return 1;
            int ifd = start + (int)offset; uint count = Read(ifd, 2);
            for (int i = 0; i < count && ifd + 2 + i * 12 + 12 <= end; i++)
            {
                int entry = ifd + 2 + i * 12;
                if (Read(entry, 2) == 274 && Read(entry + 2, 2) == 3 && Read(entry + 4, 4) == 1) return (int)Read(entry + 8, 2);
            }
            return 1;
        }
        internal static ImageHeader Read(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes, false); return ReadStream(stream);
        }
        static int Big(byte[] data,int p,int count) { int value=0; for(int i=0;i<count;i++) value=checked((value<<8)|data[p+i]); return value; }
    }
}
