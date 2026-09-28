using System;
using TMPro;
using UnityEngine;

namespace UIFrame
{
    /// <summary>
    /// 将红点路径绑定到一个子级显示对象，并可选显示聚合数量。
    /// 该组件应挂在常驻宿主上，target 不能是宿主自身或宿主的祖先。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RedDotView : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("要监听的红点路径，例如 Mail/Inbox。")]
        private string path;

        [SerializeField]
        [Tooltip("红点显示对象。必须是宿主的其他对象，通常为子节点。")]
        private GameObject target;

        [SerializeField]
        [Tooltip("可选的 TextMeshPro 数字文本。")]
        private TMP_Text tmpCountText;

        [SerializeField]
        [Min(0)]
        [Tooltip("大于该值时显示“最大值+”；0 表示不限制。")]
        private int maxCount = 99;

        private bool isBound;
        private string displayedText;

        /// <summary>当前绑定路径。</summary>
        public string Path => path;

        /// <summary>
        /// 切换绑定路径。组件激活时会立即解绑旧路径并绑定新路径。
        /// </summary>
        public void SetPath(string newPath)
        {
            if (string.IsNullOrWhiteSpace(newPath))
            {
                throw new ArgumentException("红点路径不能为空。", nameof(newPath));
            }

            RedDot.Get(newPath);

            if (string.Equals(path, newPath, StringComparison.Ordinal))
            {
                return;
            }

            Unbind();
            path = newPath;

            if (isActiveAndEnabled)
            {
                Bind();
            }
        }

        private void Reset()
        {
            if (transform.childCount > 0)
            {
                target = transform.GetChild(0).gameObject;
            }
        }

        private void OnEnable()
        {
            displayedText = null;
            Bind();
        }

        private void OnDisable()
        {
            Unbind();
        }

        private void OnDestroy()
        {
            Unbind();
        }

        private void Bind()
        {
            if (isBound)
            {
                return;
            }

            ValidateConfiguration();
            isBound = true;
            try
            {
                RedDot.Bind(path, OnCountChanged);
            }
            catch
            {
                isBound = false;
                throw;
            }
        }

        private void Unbind()
        {
            if (!isBound)
            {
                return;
            }

            RedDot.Unbind(path, OnCountChanged);
            isBound = false;
        }

        private void ValidateConfiguration()
        {
            if (target == null)
                throw new InvalidOperationException($"[{nameof(RedDotView)}] {name} 未配置红点显示对象。");
            if (target == gameObject || transform.IsChildOf(target.transform))
                throw new InvalidOperationException($"[{nameof(RedDotView)}] target 不能是宿主自身或祖先。");
            if (maxCount < 0)
                throw new ArgumentOutOfRangeException(nameof(maxCount));
            RedDot.Get(path);
        }

        private void OnCountChanged(int count)
        {
            if (target == null)
            {
                throw new InvalidOperationException($"[{nameof(RedDotView)}] 显示对象已被销毁。");
            }

            bool visible = count > 0;
            if (target.activeSelf != visible)
            {
                target.SetActive(visible);
            }

            string text = FormatCount(count);
            if (string.Equals(displayedText, text, StringComparison.Ordinal))
            {
                return;
            }

            displayedText = text;
            if (tmpCountText != null)
            {
                tmpCountText.text = text;
            }
        }

        private string FormatCount(int count)
        {
            if (count <= 0)
            {
                return string.Empty;
            }

            if (maxCount > 0 && count > maxCount)
            {
                return maxCount.ToString() + "+";
            }

            return count.ToString();
        }
    }
}
