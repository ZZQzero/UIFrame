using System;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

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
                var bytes = await UniTask.RunOnThreadPool(() =>
                {
                    if (new FileInfo(path).Length > 128L * 1024 * 1024)
                        throw new GalleryException("ImageTooLarge", "Preview encoded input is limited to 128 MiB; use file export for larger originals.");
                    return File.ReadAllBytes(path);
                }, cancellationToken: cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                ImageHeader header = ImageHeader.Read(bytes);
                // Desktop managed decoding has a deliberate bound; mobile decodes downsampled pixels natively.
                if ((long)header.Width * header.Height > 16 * 1024 * 1024)
                    throw new GalleryException("ImageTooLarge", "Desktop preview is limited to 16 megapixels; mobile uses native downsampling.");
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(texture, bytes)) throw new GalleryException("UnsupportedFormat", "Unity could not decode the image.");
                if (header.Orientation > 1) texture = Orient(texture, header.Orientation);
                if (Math.Max(texture.width, texture.height) > options.MaxEdge) texture = Resize(texture, options.MaxEdge);
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
            if (options.Mode == ImageExportMode.PreserveProvidedBytes && image.Source != "file")
            {
                var folder = ImagePaths.NewDirectory();
                var result = await NativeMedia.Request(new MediaRequest { op = "export", source = image.Source, path = image.Id, output = folder }, cancellationToken);
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
                        var pixels = preview.Texture.GetPixels(); var background = options.JpegBackground;
                        for (int i = 0; i < pixels.Length; i++) { var c = pixels[i]; pixels[i] = new Color(c.r*c.a+background.r*(1-c.a), c.g*c.a+background.g*(1-c.a), c.b*c.a+background.b*(1-c.a), 1); }
                        preview.Texture.SetPixels(pixels); preview.Texture.Apply(false);
                        bytes = ImageConversion.EncodeToJPG(preview.Texture, options.JpegQuality); path = Path.Combine(directory, "image.jpg");
                    }
                    else { bytes = ImageConversion.EncodeToPNG(preview.Texture); path = Path.Combine(directory, "image.png"); }
                    await UniTask.RunOnThreadPool(() => File.WriteAllBytes(path, bytes), cancellationToken: cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                return new ImageFile(path, new ImageStorage(directory));
            }
            catch { ImagePaths.CleanAfterFailure(directory); throw; }
        }

        static Texture2D Resize(Texture2D source, int edge)
        {
            float scale = edge / (float)Math.Max(source.width, source.height);
            int width = Math.Max(1, (int)(source.width * scale)), height = Math.Max(1, (int)(source.height * scale));
            var previous = RenderTexture.active; var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            Texture2D result = null;
            try
            {
                Graphics.Blit(source, target); RenderTexture.active = target;
                result = new Texture2D(width, height, TextureFormat.RGBA32, false); result.ReadPixels(new Rect(0, 0, width, height), 0, 0); result.Apply(false);
                ImageTexture.Destroy(source); return result;
            }
            catch { ImageTexture.Destroy(result); throw; }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); }
        }

        static Texture2D Orient(Texture2D source, int orientation)
        {
            int w = source.width, h = source.height;
            bool swap = orientation >= 5; int ow = swap ? h : w, oh = swap ? w : h;
            var input = source.GetPixels32(); var output = new Color32[input.Length];
            // Coordinates below use a top-left origin, unlike Texture2D pixel storage.
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                int tx = x, ty = y;
                switch (orientation)
                {
                    case 2: tx = w - 1 - x; break;
                    case 3: tx = w - 1 - x; ty = h - 1 - y; break;
                    case 4: ty = h - 1 - y; break;
                    case 5: tx = y; ty = x; break;
                    case 6: tx = h - 1 - y; ty = x; break;
                    case 7: tx = h - 1 - y; ty = w - 1 - x; break;
                    case 8: tx = y; ty = w - 1 - x; break;
                }
                output[(oh - 1 - ty) * ow + tx] = input[(h - 1 - y) * w + x];
            }
            var result = new Texture2D(ow, oh, TextureFormat.RGBA32, false);
            try { result.SetPixels32(output); result.Apply(false); ImageTexture.Destroy(source); return result; }
            catch { ImageTexture.Destroy(result); throw; }
        }
    }

    internal readonly struct ImageHeader
    {
        public readonly int Width, Height, Orientation;
        ImageHeader(int width, int height, int orientation) { Width = width; Height = height; Orientation = orientation; }
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
