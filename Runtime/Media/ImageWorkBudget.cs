using System;
using System.Text;

namespace Game.Media
{
    /// <summary>One admission budget for cached thumbnails, direct previews and image exports.</summary>
    internal static class ImageWorkBudget
    {
        internal const int MaximumKeys=128,MaximumWaiters=512,MaximumMetadataBytes=1024*1024;
        static int keys,waiters,metadata;
        internal static int Keys=>keys;
        internal static int Waiters=>waiters;
        internal static int MetadataBytes=>metadata;
        internal static IDisposable Acquire(ImageReference image,bool key,bool waiter)
        {
            MediaThread.Check();
            long bytes=waiter?128:0;
            if(key)
            {
                bytes+=256;
                foreach(var text in new[]{image.Source,image.Id,image.OriginId,image.Version,image.FileName,image.MimeType})if(text!=null)bytes+=48L+text.Length*2L;
            }
            if(keys+(key?1:0)>MaximumKeys || waiters+(waiter?1:0)>MaximumWaiters || bytes>MaximumMetadataBytes-metadata)
                throw new GalleryException("ImageQueueFull","Image processing admission is full. Release or await existing requests before submitting more.");
            keys+=key?1:0;waiters+=waiter?1:0;metadata+=(int)bytes;return new Reservation(key,waiter,(int)bytes);
        }
        sealed class Reservation:IDisposable
        {
            readonly bool key,waiter;int bytes;
            internal Reservation(bool key,bool waiter,int bytes){this.key=key;this.waiter=waiter;this.bytes=bytes;}
            public void Dispose(){MediaThread.Check();if(bytes==0)return;keys-=key?1:0;waiters-=waiter?1:0;metadata-=bytes;bytes=0;}
        }
    }
}
