using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Game.Media
{
    public static class GameGallery
    {
        public static GalleryCapabilities GetCapabilities()
        {
#if UNITY_EDITOR
            return new GalleryCapabilities(true, false, false, false);
#else
            return new GalleryCapabilities(NativeMedia.Available, NativeMedia.Available, NativeMedia.Available, NativeMedia.Available);
#endif
        }

        public static async UniTask<ImageSelection> PickImagesAsync(ImagePickOptions options = null, CancellationToken cancellationToken = default)
        {
            MediaThread.Check(); options ??= new ImagePickOptions(); options.Validate();
            cancellationToken.ThrowIfCancellationRequested();
#if UNITY_EDITOR
            if (options.MaxCount != 1) throw new PlatformNotSupportedException("The Editor file dialog supports one image. Use ImportFilesAsync for explicit multi-file selection.");
            var path = UnityEditor.EditorUtility.OpenFilePanelWithFilters("选择图片", "", new[] { "图片", "png,jpg,jpeg,heic,heif" });
            if (string.IsNullOrEmpty(path)) throw new OperationCanceledException();
            return await ImportFilesAsync(new[] { path }, cancellationToken);
#else
            if (!NativeMedia.Available) throw new PlatformNotSupportedException("System picker unavailable.");
            string directory = ImagePaths.NewDirectory();
            // Once submitted, native code owns cleanup until a successful response transfers it.
            var response = await NativeMedia.Request(new MediaRequest { op = "pick", count = options.MaxCount, output = directory }, cancellationToken);
            try { return Selection(response.items, directory); }
            catch { ImagePaths.CleanAfterFailure(directory); throw; }
#endif
        }

        public static async UniTask<ImageSelection> ImportFilesAsync(IReadOnlyList<string> absolutePaths, CancellationToken cancellationToken = default)
        {
            MediaThread.Check();
            if (absolutePaths == null) throw new ArgumentNullException(nameof(absolutePaths));
            if (absolutePaths.Count == 0) throw new ArgumentException("At least one file is required.", nameof(absolutePaths));
            var sources = new ImageReference[absolutePaths.Count];
            for (int i = 0; i < sources.Length; i++) sources[i] = ImageReference.FromFile(absolutePaths[i]);
            cancellationToken.ThrowIfCancellationRequested();
            string directory = ImagePaths.NewDirectory();
            try
            {
                var items = await UniTask.RunOnThreadPool(() =>
                {
                    var result = new MediaItem[sources.Length];
                    for (int i = 0; i < sources.Length; i++)
                    {
                        string output = Path.Combine(directory, i + ImagePaths.Extension(sources[i].FileName));
                        CopyFile(sources[i].Id, output, cancellationToken);
                        result[i] = new MediaItem { id = sources[i].Id, path = output, name = sources[i].FileName, mime = sources[i].MimeType, size = new FileInfo(output).Length };
                    }
                    return result;
                }, cancellationToken: cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return Selection(items, directory);
            }
            catch { ImagePaths.CleanAfterFailure(directory); throw; }
        }

        internal static void CopyFile(string source, string destination, CancellationToken token)
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[128 * 1024]; int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
            { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
            token.ThrowIfCancellationRequested(); output.Flush(true);
        }

        static ImageSelection Selection(MediaItem[] items, string directory)
        {
            if (items == null || items.Length == 0) throw new GalleryException("InvalidResult", "Picker returned no images.");
            var owner = new ImageStorage(directory); var images = new ImageReference[items.Length];
            for (int i = 0; i < images.Length; i++)
            {
                var item = items[i];
                if (!File.Exists(item.path)) throw new GalleryException("SourceUnavailable", "Selected file is missing.");
                images[i] = new ImageReference("file", item.path, item.name, item.mime, new FileInfo(item.path).Length,
                    item.width, item.height, item.version, owner, item.id ?? ("selected:" + item.name));
            }
            return new ImageSelection(images, owner);
        }

        public static UniTask<LibraryAccess> GetLibraryAccessAsync(CancellationToken cancellationToken = default) => Access(false, cancellationToken);
        public static UniTask<LibraryAccess> RequestLibraryAccessAsync(CancellationToken cancellationToken = default) => Access(true, cancellationToken);
        static async UniTask<LibraryAccess> Access(bool request, CancellationToken token)
        {
            var result = await NativeMedia.Request(new MediaRequest { op = request ? "requestAccess" : "access" }, token);
            if (!Enum.TryParse(result.access, out LibraryAccess value)) throw new GalleryException("InvalidResult", "Invalid library access response.");
            return value;
        }
        public static async UniTask<IReadOnlyList<ImageAlbum>> QueryAlbumsAsync(CancellationToken cancellationToken = default)
        {
            var result = await NativeMedia.Request(new MediaRequest { op = "albums" }, cancellationToken);
            var albums = new List<ImageAlbum>();
            foreach (var item in result.items ?? Array.Empty<MediaItem>()) albums.Add(new ImageAlbum(item.id, item.name, item.count));
            return albums.AsReadOnly();
        }
        public static async UniTask<ImageSnapshot> QueryImagesAsync(string albumId = null, CancellationToken cancellationToken = default)
        {
            var result = await NativeMedia.Request(new MediaRequest { op = "images", album = albumId }, cancellationToken);
            var items = result.items ?? Array.Empty<MediaItem>(); var images = new ImageReference[items.Length];
            long deadline = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency / 500;
            for (int i = 0; i < images.Length; i++)
            {
                if (i != 0 && i % 200 == 0 && System.Diagnostics.Stopwatch.GetTimestamp() >= deadline)
                {
                    await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
                    deadline = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency / 500;
                }
                var item = items[i]; images[i] = new ImageReference("library", item.id, item.name, item.mime, item.size, item.width, item.height, item.version);
            }
            return new ImageSnapshot(images);
        }
    }
}
