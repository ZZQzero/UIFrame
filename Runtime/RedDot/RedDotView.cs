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

        private void OnValidate()
        {
            if (maxCount < 0)
            {
                maxCount = 0;
            }
        }

        private void Bind()
        {
            if (isBound || !ValidateConfiguration())
            {
                return;
            }

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

        private bool ValidateConfiguration()
        {
            if (target == null)
            {
                Debug.LogError(
                    $"[{nameof(RedDotView)}] {name} 未配置红点显示对象。",
                    this);
                return false;
            }

            if (target == gameObject ||
                transform.IsChildOf(target.transform))
            {
                Debug.LogError(
                    $"[{nameof(RedDotView)}] target 不能是组件挂载对象自身或其祖先，" +
                    "否则隐藏 target 会导致组件退订且无法重新显示。",
                    this);
                return false;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                HideTarget();
                Debug.LogError(
                    $"[{nameof(RedDotView)}] {name} 未配置红点路径。",
                    this);
                return false;
            }

            try
            {
                RedDot.Get(path);
            }
            catch (ArgumentException exception)
            {
                HideTarget();
                Debug.LogError(
                    $"[{nameof(RedDotView)}] {name} 的红点路径无效：" +
                    exception.Message,
                    this);
                return false;
            }

            return true;
        }

        private void HideTarget()
        {
            if (target.activeSelf)
            {
                target.SetActive(false);
            }
        }

        private void OnCountChanged(int count)
        {
            if (target == null)
            {
                Unbind();
                return;
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
