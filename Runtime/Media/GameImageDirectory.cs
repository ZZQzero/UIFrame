using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Game.Media
{
    public static class GameImageDirectory
    {
        public static async UniTask<ImageDirectoryHandle> PickDirectoryAsync(CancellationToken cancellationToken = default)
        {
            var response = await NativeMedia.Request(new MediaRequest { op = "pickDirectory" }, cancellationToken);
            return ImageDirectoryHandle.FromBookmark(response.items[0].id);
        }

        public static async UniTask<ImageSnapshot> QueryAsync(string absoluteDirectory, bool recursive = false,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(absoluteDirectory) || !Path.IsPathRooted(absoluteDirectory))
                throw new ArgumentException("An absolute directory is required.", nameof(absoluteDirectory));
            var root = Path.GetFullPath(absoluteDirectory);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            return await UniTask.RunOnThreadPool(() =>
            {
                var images = new List<ImageReference>(); var pending = new Stack<string>(); pending.Push(root);
                while (pending.Count != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string folder = pending.Pop();
                    foreach (var path in Directory.EnumerateFileSystemEntries(folder))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attributes & FileAttributes.Directory) != 0) { if (recursive) pending.Push(path); continue; }
                        if (ImagePaths.Mime(Path.GetExtension(path)).StartsWith("image/", StringComparison.Ordinal))
                            images.Add(ImageReference.FromFile(path));
                    }
                }
                images.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
                return new ImageSnapshot(images.ToArray());
            }, cancellationToken: cancellationToken);
        }
    }

    /// <summary>Persist Bookmark if the user wants to reuse a granted directory. It is not a filesystem path.</summary>
    public sealed class ImageDirectoryHandle
    {
        public string Bookmark { get; }
        ImageDirectoryHandle(string bookmark) { Bookmark = bookmark; }
        public static ImageDirectoryHandle FromBookmark(string bookmark)
        {
            if (string.IsNullOrWhiteSpace(bookmark)) throw new ArgumentException("Directory bookmark required.", nameof(bookmark));
            return new ImageDirectoryHandle(bookmark);
        }
        public async UniTask<ImageSnapshot> QueryAsync(bool recursive = false, CancellationToken cancellationToken = default)
        {
            var response = await NativeMedia.Request(new MediaRequest { op = "directory", path = Bookmark, recursive = recursive }, cancellationToken);
            var result = new List<ImageReference>();
            foreach (var item in response.items ?? Array.Empty<MediaItem>())
                result.Add(new ImageReference(item.source ?? "directory", item.id, item.name, item.mime, item.size, item.width, item.height, item.version));
            return new ImageSnapshot(result.ToArray());
        }
    }
}
