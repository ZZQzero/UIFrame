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
        public static UniTask<ImageTexture> LoadThumbnailAsync(ImageReference image, int maxEdge = 256, CancellationToken cancellationToken = default)
            => LoadPreviewAsync(image, new ImagePreviewOptions { MaxEdge = maxEdge }, cancellationToken);

        public static async UniTask<ImageTexture> LoadPreviewAsync(ImageReference image, ImagePreviewOptions options = null, CancellationToken cancellationToken = default)
        {
            MediaThread.Check(); if (image == null) throw new ArgumentNullException(nameof(image));
            options ??= new ImagePreviewOptions(); options.Validate(); cancellationToken.ThrowIfCancellationRequested();
            using var lease = image.Acquire();
            string nativeDirectory = null; string path = image.Id; Texture2D texture = null;
            bool failed = false;
            try
            {
                if (NativeMedia.Available)
                {
                    string output = ImagePaths.NewDirectory();
                    var result = await NativeMedia.Request(new MediaRequest { op = "preview", source = image.Source, path = image.Id, output = output, edge = options.MaxEdge }, cancellationToken);
                    nativeDirectory = output; path = result.items[0].path;
                }
                else if (image.Source != "file") throw new PlatformNotSupportedException("Native source unavailable.");
                var header = await UniTask.RunOnThreadPool(() => ImageHeader.ReadFile(path), cancellationToken: cancellationToken);
                if (!NativeMedia.Available && (long)header.Width * header.Height > 16 * 1024 * 1024)
                    throw new GalleryException("ImageTooLarge", "Desktop preview is limited to 16 megapixels; mobile uses native downsampling.");
                // DownloadHandlerTexture performs image decoding on Unity's worker thread.
                using (var request = UnityWebRequestTexture.GetTexture(new Uri(path).AbsoluteUri))
                {
                    await request.SendWebRequest().ToUniTask(cancellationToken: cancellationToken);
                    texture = DownloadHandlerTexture.GetContent(request);
                }
                if (header.Orientation > 1 || Math.Max(texture.width, texture.height) > options.MaxEdge)
                {
                    var transformed = RenderImage(texture, header.Orientation, options.MaxEdge, false, default);
                    ImageTexture.Destroy(texture); texture = transformed;
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (nativeDirectory != null) { Directory.Delete(nativeDirectory, true); nativeDirectory = null; }
                var resource = new ImageTexture(texture); texture = null; return resource;
            }
            catch { failed = true; throw; }
            finally
            {
                ImageTexture.Destroy(texture);
                if (nativeDirectory != null && failed) ImagePaths.CleanAfterFailure(nativeDirectory);
            }
        }

        public static async UniTask<ImageFile> ExportFileAsync(ImageReference image, ImageExportOptions options = null, CancellationToken cancellationToken = default)
        {
            MediaThread.Check(); if (image == null) throw new ArgumentNullException(nameof(image));
            options ??= new ImageExportOptions(); options.Validate(); cancellationToken.ThrowIfCancellationRequested();
            using var lease = image.Acquire();
            if (NativeMedia.Available && (options.Mode != ImageExportMode.PreserveProvidedBytes || image.Source != "file"))
            {
                var folder = ImagePaths.NewDirectory();
                var result = await NativeMedia.Request(new MediaRequest { op = options.Mode == ImageExportMode.PreserveProvidedBytes ? "export" : "preview",
                    source = image.Source, path = image.Id, output = folder, edge = options.MaxEdge, quality = options.JpegQuality,
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
                    await UniTask.RunOnThreadPool(() => GameGallery.CopyFile(image.Id, path, cancellationToken), cancellationToken: cancellationToken);
                }
                else
                {
                    using var preview = await LoadPreviewAsync(image, new ImagePreviewOptions { MaxEdge = options.MaxEdge }, cancellationToken);
                    byte[] bytes;
                    if (options.Mode == ImageExportMode.Jpeg)
                    {
                        var flattened = RenderImage(preview.Texture, 1, options.MaxEdge, true, options.JpegBackground);
                        try { bytes = ImageConversion.EncodeToJPG(flattened, options.JpegQuality); }
                        finally { ImageTexture.Destroy(flattened); }
                        path = Path.Combine(directory, "image.jpg");
                    }
                    else { bytes = ImageConversion.EncodeToPNG(preview.Texture); path = Path.Combine(directory, "image.png"); }
                    await UniTask.RunOnThreadPool(() => File.WriteAllBytes(path, bytes), cancellationToken: cancellationToken);
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
        internal static ImageHeader ReadFile(string path)
        {
            using var input = File.OpenRead(path);
            if (input.Length > 128L * 1024 * 1024)
                throw new GalleryException("ImageTooLarge", "Preview encoded input is limited to 128 MiB; use file export for larger originals.");
            using var header = new MemoryStream();
            int first = input.ReadByte(), second = input.ReadByte();
            if (first == 137 && second == 80)
            {
                input.Position = 0; var png = new byte[24]; int count = input.Read(png, 0, png.Length);
                if (count != png.Length) throw new GalleryException("InvalidImage", "Truncated PNG header.");
                return Read(png);
            }
            if (first != 255 || second != 216) throw new GalleryException("UnsupportedFormat", "Managed preview supports JPEG and PNG.");
            header.WriteByte(255); header.WriteByte(216);
            // Keep only the header needed for dimensions/EXIF, never the compressed pixel payload.
            while (input.Position < input.Length)
            {
                int prefix = input.ReadByte(), marker = input.ReadByte();
                if (prefix != 255 || marker < 0) break;
                while (marker == 255) marker = input.ReadByte();
                if (marker < 0 || marker == 217 || marker == 218) break;
                int high = input.ReadByte(), low = input.ReadByte();
                if (high < 0 || low < 0) break;
                int length = (high << 8) | low;
                if (length < 2 || input.Position + length - 2 > input.Length) break;
                // Other segments (e.g. ICC profiles) need no managed copy.
                if (marker >= 192 && marker <= 195 || marker == 225)
                {
                    header.WriteByte(255); header.WriteByte((byte)marker); header.WriteByte((byte)high); header.WriteByte((byte)low);
                    var segment = new byte[length - 2]; int read = 0;
                    while (read < segment.Length) { int n = input.Read(segment, read, segment.Length - read); if (n == 0) break; read += n; }
                    header.Write(segment, 0, read);
                }
                else input.Seek(length - 2, SeekOrigin.Current);
            }
            return Read(header.ToArray());
        }
        internal static ImageHeader Read(byte[] bytes)
        {
            if (bytes.Length >= 24 && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71)
            {
                int width = Big(bytes,16,4), height = Big(bytes,20,4);
                if (width <= 0 || height <= 0) throw new GalleryException("InvalidImage", "Invalid PNG dimensions.");
                return new ImageHeader(width, height, 1);
            }
            if (bytes.Length < 4 || bytes[0] != 255 || bytes[1] != 216) throw new GalleryException("UnsupportedFormat", "Managed preview supports JPEG and PNG.");
            int w = 0, h = 0, orientation = 1;
            for (int p = 2; p + 4 <= bytes.Length;)
            {
                if (bytes[p++] != 255) break;
                while (p < bytes.Length && bytes[p] == 255) p++;
                if (p >= bytes.Length) break; int marker = bytes[p++];
                if (marker == 217 || marker == 218) break;
                int length = Big(bytes,p,2); if (length < 2 || p + length > bytes.Length) break;
                if (marker >= 192 && marker <= 195 && length >= 8) { h = Big(bytes,p+3,2); w = Big(bytes,p+5,2); }
                if (marker == 225 && length > 16 && bytes[p+2] == 69 && bytes[p+3] == 120 && bytes[p+4] == 105 && bytes[p+5] == 102)
                {
                    int start = p+8, end = p+length; bool little = bytes[start] == 73;
                    uint Read(int offset, int size)
                    {
                        if (offset < start || offset > end-size) throw new GalleryException("InvalidImage", "Invalid EXIF offset.");
                        uint v=0; for(int j=0;j<size;j++) v=(v<<8)|bytes[offset+(little ? size-1-j : j)]; return v;
                    }
                    uint offset = Read(start+4,4);
                    if (offset <= length-10)
                    {
                        int ifd = start+(int)offset; uint count = Read(ifd,2);
                        for (int i=0;i<count && ifd+2+i*12+12<=end;i++)
                        {
                            int entry=ifd+2+i*12;
                            if (Read(entry,2)==274 && Read(entry+2,2)==3 && Read(entry+4,4)==1) orientation=(int)Read(entry+8,2);
                        }
                    }
                }
                p += length;
            }
            if(w<=0 || h<=0) throw new GalleryException("InvalidImage", "JPEG dimensions missing.");
            return new ImageHeader(w,h,orientation >= 1 && orientation <= 8 ? orientation : 1);
        }
        static int Big(byte[] data,int p,int count) { int value=0; for(int i=0;i<count;i++) value=checked((value<<8)|data[p+i]); return value; }
    }
}
