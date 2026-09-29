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
            var images = new List<ImageReference>();
            await VisitAsync(absoluteDirectory, page => { images.AddRange(page); return UniTask.FromResult(true); }, recursive, cancellationToken);
            images.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
            return new ImageSnapshot(images.ToArray());
        }
        public static async UniTask<bool> VisitAsync(string absoluteDirectory, Func<IReadOnlyList<ImageReference>, UniTask<bool>> consume,
            bool recursive = false, CancellationToken cancellationToken = default)
        {
            MediaThread.Check();
            if (consume == null) throw new ArgumentNullException(nameof(consume));
            if (string.IsNullOrWhiteSpace(absoluteDirectory) || !Path.IsPathRooted(absoluteDirectory))
                throw new ArgumentException("An absolute directory is required.", nameof(absoluteDirectory));
            string root = Path.GetFullPath(absoluteDirectory);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            using var iterator = Enumerate(root, recursive, cancellationToken).GetEnumerator();
            bool more = true;
            while (more)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await UniTask.RunOnThreadPool(() =>
                {
                    var result = new List<ImageReference>(200);
                    while (result.Count < 200 && (more = iterator.MoveNext())) result.Add(iterator.Current);
                    return result.AsReadOnly();
                });
                cancellationToken.ThrowIfCancellationRequested();
                if (page.Count != 0 && !await consume(page)) return false;
            }
            return true;
        }
        static IEnumerable<ImageReference> Enumerate(string root, bool recursive, CancellationToken token)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(root))
            {
                token.ThrowIfCancellationRequested(); var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (recursive) foreach (var image in Enumerate(path, true, token)) yield return image;
                }
                else if (ImagePaths.Mime(Path.GetExtension(path)).StartsWith("image/", StringComparison.Ordinal)) yield return ImageReference.FromFile(path);
            }
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
            var images = new List<ImageReference>();
            await VisitAsync(page => { images.AddRange(page); return UniTask.FromResult(true); }, recursive, cancellationToken);
            return new ImageSnapshot(images.ToArray());
        }
        public async UniTask<bool> VisitAsync(Func<IReadOnlyList<ImageReference>, UniTask<bool>> consume, bool recursive = false,
            CancellationToken cancellationToken = default)
        {
            if (consume == null) throw new ArgumentNullException(nameof(consume));
            bool completed = true;
            await NativeMedia.Request(new MediaRequest { op = "directory", path = Bookmark, recursive = recursive }, cancellationToken,
                async items => completed = await GameGallery.ConsumePage(items, "directory", consume));
            return completed;
        }
    }
}
