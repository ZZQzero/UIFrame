using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using Cysharp.Threading.Tasks;
using Game.Media;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Uses UIFrame LoopScroll for visible cells and a bounded 200-item metadata page.</summary>
public sealed class GalleryVirtualListDemo : MonoBehaviour,LoopScrollDataSource,LoopScrollPrefabSource
{
    [SerializeField] LoopVerticalScrollRect scroll;
    [SerializeField] GalleryThumbnailCell cellPrefab;
    readonly Stack<GalleryThumbnailCell> pool=new Stack<GalleryThumbnailCell>();
    readonly List<GalleryThumbnailCell> cells=new List<GalleryThumbnailCell>();
    IReadOnlyList<ImageReference> items=Array.Empty<ImageReference>();
    ImageThumbnailCache cache;
    ImageLibraryIndex library;
    ImageLibraryScope scope;
    ImageLibraryCursor next;
    bool busy,closing,closed;
    public bool HasNextPage=>next!=null;
    public async UniTask InitializeAsync(ImageLibraryIndex index,ImageLibraryScope source)
    {
        if(cache!=null || closing)throw new InvalidOperationException("Gallery list is already initialized or closed.");
        if(scroll==null || cellPrefab==null)throw new InvalidOperationException("Configure LoopVerticalScrollRect and cell prefab.");
        library=index??throw new ArgumentNullException(nameof(index));scope=source??throw new ArgumentNullException(nameof(source));
        cache=new ImageThumbnailCache();scroll.dataSource=this;scroll.prefabSource=this;
        await ShowFirstPageAsync();
    }
    public UniTask ShowFirstPageAsync()=>LoadPage(null);
    public UniTask ShowNextPageAsync()
    {if(next==null)throw new InvalidOperationException("There is no next page.");return LoadPage(next);}
    async UniTask LoadPage(ImageLibraryCursor cursor)
    {
        if(cache==null || closing)throw new InvalidOperationException("Gallery list is unavailable.");
        if(busy)throw new InvalidOperationException("A page request is already active.");busy=true;
        try {
            var page=await library.QueryAsync(scope,200,cursor);
            if(closing)return;
            scroll.ClearCells();items=page.Items;next=page.Next;scroll.totalCount=items.Count;scroll.RefillCells();
        } finally {busy=false;}
    }
    public GameObject GetObject(int index)
    {
        GalleryThumbnailCell cell;
        if(pool.Count!=0)cell=pool.Pop();else {cell=Instantiate(cellPrefab,scroll.content);cells.Add(cell);}
        cell.transform.SetParent(scroll.content,false);cell.gameObject.SetActive(true);return cell.gameObject;
    }
    public void ReturnObject(Transform transform)
    {
        var cell=transform.GetComponent<GalleryThumbnailCell>();cell.Unbind();cell.gameObject.SetActive(false);
        cell.transform.SetParent(this.transform,false);pool.Push(cell);
    }
    public void ProvideData(Transform transform,int index)
        => transform.GetComponent<GalleryThumbnailCell>().BindAsync(cache,items[index]).Forget(Debug.LogException);
    public async UniTask ShutdownAsync()
    {
        if(closed)return;if(closing)throw new InvalidOperationException("Gallery list shutdown is active.");closing=true;
        while(busy)await UniTask.Yield();
        ExceptionDispatchInfo failure=null;
        void Capture(Exception error){if(failure==null)failure=ExceptionDispatchInfo.Capture(error);else Debug.LogException(error);}
        void Clean(Action action){try{action();}catch(Exception error){Capture(error);}}
        if(scroll!=null){Clean(scroll.ClearCells);scroll.dataSource=null;scroll.prefabSource=null;}
        foreach(var cell in cells)if(cell!=null){Clean(cell.Unbind);Destroy(cell.gameObject);}
        cells.Clear();pool.Clear();items=Array.Empty<ImageReference>();
        if(cache!=null)try{await cache.ShutdownAsync();}catch(Exception error){Capture(error);}
        closed=true;failure?.Throw();
    }
    void OnDestroy(){if(!closed && !closing)ShutdownAsync().Forget(Debug.LogException);}
}
