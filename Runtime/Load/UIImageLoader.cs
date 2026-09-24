using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using YooAsset;

namespace UIFrame
{
    /// <summary>
    /// 为 UGUI Image 加载 YooAsset Sprite。组件只持有当前图片的资源句柄。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Image))]
    public sealed class UIImageLoader : MonoBehaviour
    {
        [SerializeField] Image _target;
        [SerializeField] Sprite _placeholder;
        [SerializeField] Sprite _error;

        AssetHandle _handle;
        CancellationTokenSource _requestCts;
        int _requestVersion;

        public Image Target => _target != null ? _target : GetComponent<Image>();

        public Sprite Placeholder
        {
            get => _placeholder;
            set => _placeholder = value;
        }

        public Sprite Error
        {
            get => _error;
            set => _error = value;
        }

        void Awake()
        {
            _target ??= GetComponent<Image>();
        }

        public async UniTask LoadAsync(string location, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(location))
                throw new ArgumentException("[UIFrame] 图片资源地址为空。", nameof(location));

            Image target = Target;
            if (target == null)
                throw new InvalidOperationException("[UIFrame] UIImageLoader 缺少 Image 目标。");

            CancelRequest();
            ReleaseCurrent();
            target.sprite = _placeholder;

            int version = ++_requestVersion;
            var requestCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                destroyCancellationToken);
            _requestCts = requestCts;
            AssetHandle handle = null;
            try
            {
                handle = await UI.LoadAsset<Sprite>(location, requestCts.Token);
                requestCts.Token.ThrowIfCancellationRequested();

                Sprite sprite = handle.GetAssetObject<Sprite>();
                if (sprite == null)
                    throw new InvalidOperationException(
                        $"[UIFrame] 图片资源不是有效 Sprite: {location}");
                if (!IsCurrent(version, requestCts))
                    return;

                _handle = handle;
                handle = null;
                target.sprite = sprite;
            }
            catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                if (IsCurrent(version, requestCts))
                    target.sprite = _error;
                throw;
            }
            finally
            {
                UILoader.Release(handle);
                if (ReferenceEquals(_requestCts, requestCts))
                    _requestCts = null;
                requestCts.Dispose();
            }
        }

        public UniTask LoadAsync(string location, UIFrameScope scope)
        {
            if (scope == null)
                throw new ArgumentNullException(nameof(scope));

            scope.Register(Clear);
            return LoadAsync(location, scope.Token);
        }

        public void Clear()
        {
            CancelRequest();
            ReleaseCurrent();
            Image target = Target;
            if (target != null)
                target.sprite = _placeholder;
        }

        void CancelRequest()
        {
            _requestVersion++;
            var cts = _requestCts;
            _requestCts = null;
            if (cts == null)
                return;
            cts.Cancel();
        }

        void ReleaseCurrent()
        {
            var handle = _handle;
            _handle = null;
            UILoader.Release(handle);
        }

        bool IsCurrent(int version, CancellationTokenSource cts)
        {
            return version == _requestVersion
                && ReferenceEquals(_requestCts, cts)
                && !cts.IsCancellationRequested;
        }

        void OnDestroy()
        {
            CancelRequest();
            ReleaseCurrent();
        }
    }
}
