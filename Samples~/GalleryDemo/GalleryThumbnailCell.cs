using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Media;
using UnityEngine;
using UnityEngine.UI;

/// <summary>One visible cell owns one cache lease; rebinding never accepts an older request.</summary>
public sealed class GalleryThumbnailCell : MonoBehaviour
{
    [SerializeField] RawImage target;
    CancellationTokenSource binding;
    ImageTexture lease;
    public async UniTask BindAsync(ImageThumbnailCache cache,ImageReference image)
    {
        if(target==null)throw new InvalidOperationException("Configure the cell RawImage.");
        Unbind();var request=new CancellationTokenSource();binding=request;
        ImageTexture loaded=null;
        try {
            loaded=await cache.AcquireAsync(image,new ImagePreviewOptions {MaxEdge=256,MaxPixels=256*256},request.Token);
            if(binding!=request || request.IsCancellationRequested)return;
            target.texture=loaded.Texture;lease=loaded;loaded=null;
        }
        catch(OperationCanceledException) when(request.IsCancellationRequested) { }
        finally {if(binding==request)binding=null;request.Dispose();loaded?.Dispose();}
    }
    public void Unbind()
    {
        var request=binding;binding=null;var previous=lease;lease=null;
        if(target!=null)target.texture=null;
        try {request?.Cancel();}finally {previous?.Dispose();}
    }
    void OnDisable()=>Unbind();
    void OnDestroy()=>Unbind();
}
