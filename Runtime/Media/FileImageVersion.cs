using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace Game.Media
{
    // A file metadata reference is cheap to create. The persistent directory index
    // additionally proves content, and reuses that proof only under continuous observation.
    internal static class FileImageVersion
    {
        const string Separator = ":sha256:";
        internal static bool MatchesMetadata(ImageReference image,string version)
            => version != null && version.StartsWith(image.Version+Separator,StringComparison.Ordinal);
        internal static ImageReference WithVersion(ImageReference image,string version)
            => new ImageReference("file",image.Id,image.FileName,image.MimeType,image.ByteCount??-1,version:version);
        internal static ImageReference Read(ImageReference image,CancellationToken token)
        {
            using var input=File.OpenRead(image.Id);
            using var sha=SHA256.Create();
            var buffer=new byte[128*1024];int count;
            while((count=input.Read(buffer,0,buffer.Length))!=0)
            {token.ThrowIfCancellationRequested();sha.TransformBlock(buffer,0,count,null,0);}
            token.ThrowIfCancellationRequested();sha.TransformFinalBlock(Array.Empty<byte>(),0,0);
            if(ImageReference.FromFile(image.Id).Version!=image.Version)
                throw new GalleryException("SourceChanged","File changed while its content version was being read.");
            return WithVersion(image,image.Version+Separator+BitConverter.ToString(sha.Hash).Replace("-","").ToLowerInvariant());
        }
        internal static string Current(ImageReference image,CancellationToken token)
        {
            var metadata=ImageReference.FromFile(image.Id);
            return image.Version.Contains(Separator)?Read(metadata,token).Version:metadata.Version;
        }
    }
}
