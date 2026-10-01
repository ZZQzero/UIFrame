using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Game.Media
{
    public enum LibraryAccess { NotDetermined, Denied, Limited, Authorized, Restricted }
    public enum ImageExportMode { PreserveProvidedBytes, Jpeg, Png }

    public sealed class GalleryException : Exception
    {
        public string Code { get; }
        internal bool IsScopeAccessFailure => Code == "PermissionDenied" || Code == "ScopeConfirmationRequired";
        public GalleryException(string code, string message) : base(message) { Code = code; }
    }

    public readonly struct GalleryCapabilities
    {
        public bool CanPick { get; }
        public bool CanPickMultiple { get; }
        public bool CanQueryLibrary { get; }
        public bool CanPickDirectory { get; }
        internal GalleryCapabilities(bool pick, bool multiple, bool library, bool directory)
        { CanPick = pick; CanPickMultiple = multiple; CanQueryLibrary = library; CanPickDirectory = directory; }
    }

    public sealed class ImagePickOptions
    {
        public int MaxCount { get; set; } = 1;
        internal void Validate() { if (MaxCount < 1) throw new ArgumentOutOfRangeException(nameof(MaxCount)); }
    }

    public sealed class ImagePreviewOptions
    {
        public int MaxEdge { get; set; } = 2048;
        public int MaxPixels { get; set; } = 4 * 1024 * 1024;
        public bool Readable { get; set; }
        internal void Validate()
        {
            if (MaxEdge < 1 || MaxEdge > 8192) throw new ArgumentOutOfRangeException(nameof(MaxEdge), "Expected 1..8192 pixels.");
            if (MaxPixels < 1 || MaxPixels > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(MaxPixels), "Expected 1..16777216 pixels.");
        }
    }

    public sealed class ImageExportOptions
    {
        public ImageExportMode Mode { get; set; } = ImageExportMode.PreserveProvidedBytes;
        public int MaxEdge { get; set; } = 2048;
        public int MaxPixels { get; set; } = 4 * 1024 * 1024;
        public int JpegQuality { get; set; } = 90;
        public Color JpegBackground { get; set; } = Color.white;
        internal void Validate()
        {
            if (!Enum.IsDefined(typeof(ImageExportMode), Mode)) throw new ArgumentOutOfRangeException(nameof(Mode));
            if (Mode == ImageExportMode.PreserveProvidedBytes) return;
            new ImagePreviewOptions { MaxEdge = MaxEdge, MaxPixels = MaxPixels }.Validate();
            if (Mode == ImageExportMode.Jpeg && (JpegQuality < 1 || JpegQuality > 100))
                throw new ArgumentOutOfRangeException(nameof(JpegQuality));
        }
    }

    public sealed class ImageReference
    {
        internal readonly string Source;
        internal readonly ImageStorage Owner;
        public string Id { get; }
        public string OriginId { get; }
        public string FileName { get; }
        public string MimeType { get; }
        public long? ByteCount { get; }
        public int? Width { get; }
        public int? Height { get; }
        public string Version { get; }

        internal ImageReference(string source, string id, string name, string mime, long size = -1,
            int width = 0, int height = 0, string version = null, ImageStorage owner = null, string originId = null)
        {
            Source = source; Id = source == "directory" ? ImageIdentity.Directory(id) : id; OriginId = originId ?? Id; FileName = name; MimeType = mime; Version = version;
            ByteCount = size < 0 ? (long?)null : size; Width = width > 0 ? (int?)width : null;
            Height = height > 0 ? (int?)height : null; Owner = owner;
        }

        public static ImageReference FromFile(string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath) || !Path.IsPathRooted(absolutePath))
                throw new ArgumentException("An absolute file path is required.", nameof(absolutePath));
            var file = new FileInfo(absolutePath);
            if (!file.Exists) throw new FileNotFoundException("Image file does not exist.", absolutePath);
            return new ImageReference("file", file.FullName, file.Name, ImagePaths.Mime(file.Extension),
                file.Length, version: file.LastWriteTimeUtc.Ticks + ":" + file.Length);
        }

        internal IDisposable Acquire() => Owner?.Acquire();
    }

    public sealed class ImageAlbum
    {
        public string Id { get; }
        public string Name { get; }
        public int? Count { get; }
        internal ImageAlbum(string id, string name, int count) { Id = id; Name = name; Count = count < 0 ? (int?)null : count; }
    }

    /// <summary>Metadata snapshot. Reading a source later may fail if it was removed or permission changed.</summary>
    public sealed class ImageSnapshot
    {
        readonly ImageReference[] images;
        public int Count => images.Length;
        internal ImageSnapshot(ImageReference[] images) { this.images = images; }
        public IReadOnlyList<ImageReference> GetPage(int offset = 0, int pageSize = 60)
        {
            if (offset < 0 || offset > Count) throw new ArgumentOutOfRangeException(nameof(offset));
            if (pageSize < 1 || pageSize > 200) throw new ArgumentOutOfRangeException(nameof(pageSize));
            int count = Math.Min(pageSize, Count - offset);
            var result = new ImageReference[count]; Array.Copy(images, offset, result, 0, count);
            return Array.AsReadOnly(result);
        }
    }
    // Canonicalize the current directory identity because native JSON property order is unspecified.
    internal static class ImageIdentity
    {
        [Serializable] sealed class DirectoryIdentity { public string bookmarkId, relative; }
        internal static string Directory(string value)
        {
            if (string.IsNullOrEmpty(value) || value[0] != '{') return value; // Android content URI.
            var id = JsonUtility.FromJson<DirectoryIdentity>(value);
            if (string.IsNullOrEmpty(id.relative) || string.IsNullOrEmpty(id.bookmarkId))
                throw new GalleryException("InvalidImage", "Invalid directory image identity.");
            return JsonUtility.ToJson(id);
        }
    }
}
