using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;
using UnityEngine.InputSystem;

[assembly: InternalsVisibleTo("Input.EditMode.Tests")]
[assembly: InternalsVisibleTo("Input.PlayMode.Tests")]

namespace Game.Input
{
    /// <summary>
    /// 进程内输入入口。Launch 显式 Init / Shutdown。
    /// 玩法轮询走 Player，开界面 PushUi，暂停 SetGameplayEnabled。
    /// </summary>
    public static class GameInput
    {
        static readonly InputUiLockStack UiLocks = new();

        static InputActionAsset runtimeAsset;
        static InputActionMap gameplayMap;
        static InputActionMap uiMap;
        static InputAction uiNavigate;
        static PlayerControls player;
        static UiControls ui;
        static bool keepUiMapEnabled;
        static bool gameplayEnabled = true;
        static bool running;
        static int mainThreadId;

        public static bool IsInited => running;

        /// <summary>
        /// 暂停开关。玩法 Map 实际 Enable 还要 <see cref="UiLockCount"/> 为 0。
        /// </summary>
        public static bool IsGameplayEnabled
        {
            get
            {
                RequireRunning(nameof(IsGameplayEnabled));
                return gameplayEnabled;
            }
        }

        public static PlayerControls Player
        {
            get
            {
                RequireRunning(nameof(Player));
                return player;
            }
        }

        public static UiControls UI
        {
            get
            {
                RequireRunning(nameof(UI));
                return ui;
            }
        }

        public static float LookSensitivity
        {
            get
            {
                RequireRunning(nameof(LookSensitivity));
                return player.LookSensitivity;
            }
            set
            {
                RequireMainThread(nameof(LookSensitivity));
                RequireRunning(nameof(LookSensitivity));
                if (!float.IsFinite(value) || value <= 0f)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value),
                        "LookSensitivity 必须是大于 0 的有限值。");
                }

                player.LookSensitivity = value;
            }
        }

        public static int UiLockCount
        {
            get
            {
                RequireRunning(nameof(UiLockCount));
                return UiLocks.Count;
            }
        }

        public static void Init(
            Transform persistRoot,
            InputRuntimeConfig runtimeConfig)
        {
            RequireMainThread(nameof(Init));
            if (runtimeConfig == null)
            {
                throw new ArgumentNullException(nameof(runtimeConfig));
            }

            runtimeConfig.Validate();
            Init(
                persistRoot,
                runtimeConfig.Actions,
                runtimeConfig.GameplayMap,
                runtimeConfig.UiMap,
                runtimeConfig.KeepUiMapEnabled,
                runtimeConfig.LookSensitivity);
        }

        public static void Init(Transform persistRoot, InputActionAsset actions)
        {
            RequireMainThread(nameof(Init));
            ValidateAsset(actions, PlayerActions.Map, UiActions.Map, 1f);
            Init(
                persistRoot,
                actions,
                PlayerActions.Map,
                UiActions.Map,
                keepUi: true,
                lookSensitivity: 1f);
        }

        public static InputLayerHandle PushUi()
        {
            RequireMainThread(nameof(PushUi));
            RequireRunning(nameof(PushUi));
            InputLayerHandle handle = UiLocks.Push();
            ApplyMaps();
            return handle;
        }

        public static void PopUi(InputLayerHandle handle)
        {
            RequireMainThread(nameof(PopUi));
            RequireRunning(nameof(PopUi));
            UiLocks.Pop(handle);
            ApplyMaps();
        }

        public static bool TryPopUi(InputLayerHandle handle)
        {
            RequireMainThread(nameof(TryPopUi));
            RequireRunning(nameof(TryPopUi));
            if (!UiLocks.TryPop(handle))
            {
                return false;
            }

            ApplyMaps();
            return true;
        }

        public static void SetGameplayEnabled(bool enabled)
        {
            RequireMainThread(nameof(SetGameplayEnabled));
            RequireRunning(nameof(SetGameplayEnabled));
            if (gameplayEnabled == enabled)
            {
                return;
            }

            gameplayEnabled = enabled;
            ApplyMaps();
        }

        public static InputAction Find(string map, string action)
        {
            RequireMainThread(nameof(Find));
            RequireRunning(nameof(Find));
            if (string.IsNullOrWhiteSpace(map))
            {
                throw new ArgumentException("Map 不能为空。", nameof(map));
            }

            if (string.IsNullOrWhiteSpace(action))
            {
                throw new ArgumentException("Action 不能为空。", nameof(action));
            }

            InputActionMap actionMap = runtimeAsset.FindActionMap(map);
            if (actionMap == null)
            {
                throw new KeyNotFoundException(
                    $"InputActionAsset 中不存在 Action Map：{map}。");
            }

            InputAction found = actionMap.FindAction(action);
            if (found == null)
            {
                throw new KeyNotFoundException(
                    $"Action Map '{map}' 中不存在动作：{action}。");
            }

            return found;
        }

        public static void Shutdown()
        {
            RequireMainThread(nameof(Shutdown));
            if (!running)
            {
                throw new InputStateException(
                    "GameInput.Shutdown 只能在成功 Init 后调用一次。");
            }

            InputActionAsset clone = runtimeAsset;
            ClearRuntimeState();
            try
            {
                if (clone != null)
                {
                    clone.Disable();
                }
            }
            finally
            {
                DestroyObject(clone);
            }
        }

        internal static void ValidateAsset(
            InputActionAsset actions,
            string gameplayMapName,
            string uiMapName,
            float lookSensitivity)
        {
            if (actions == null)
            {
                throw new InvalidOperationException(
                    "必须指定 InputActionAsset。");
            }

            if (string.IsNullOrWhiteSpace(gameplayMapName))
            {
                throw new InvalidOperationException(
                    "gameplayMap 不能为空。");
            }

            InputActionMap foundGameplay = actions.FindActionMap(gameplayMapName);
            if (foundGameplay == null)
            {
                throw new InvalidOperationException(
                    $"InputActionAsset 中不存在 gameplayMap：{gameplayMapName}。");
            }

            if (!string.IsNullOrWhiteSpace(uiMapName))
            {
                InputActionMap foundUi = actions.FindActionMap(uiMapName);
                if (foundUi == null)
                {
                    throw new InvalidOperationException(
                        $"InputActionAsset 中不存在 uiMap：{uiMapName}。");
                }

                if (foundUi == foundGameplay)
                {
                    throw new InvalidOperationException(
                        "gameplayMap 与 uiMap 不能指向同一张 Action Map。");
                }
            }

            if (!float.IsFinite(lookSensitivity) || lookSensitivity <= 0f)
            {
                throw new InvalidOperationException(
                    "lookSensitivity 必须是大于 0 的有限值。");
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetOnDomainReload()
        {
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            InputActionAsset clone = runtimeAsset;
            ClearRuntimeState();
            DestroyObject(clone);
        }

        static void Init(
            Transform persistRoot,
            InputActionAsset source,
            string gameplayMapName,
            string uiMapName,
            bool keepUi,
            float lookSensitivity)
        {
            if (persistRoot == null)
            {
                throw new ArgumentNullException(nameof(persistRoot));
            }

            if (!persistRoot.gameObject.activeInHierarchy)
            {
                throw new InputStateException(
                    "GameInput.Init 要求 persistRoot 处于 activeInHierarchy 状态。");
            }

            if (running)
            {
                throw new InputStateException(
                    "GameInput.Init 重复调用或上次未按流程 Shutdown。");
            }

            InputActionAsset clone = UnityEngine.Object.Instantiate(source);
            clone.name = source.name + " (Runtime)";
            try
            {
                InputActionMap clonedGameplay = clone.FindActionMap(gameplayMapName)
                    ?? throw new InputStateException(
                        $"克隆后的 Asset 中不存在 gameplayMap：{gameplayMapName}。");
                InputAction move = clonedGameplay.FindAction(PlayerActions.Move);
                if (move == null)
                {
                    throw new InputStateException(
                        $"gameplay Map '{gameplayMapName}' 必须包含动作 '{PlayerActions.Move}'。");
                }

                InputAction look = FindOptional(clonedGameplay, PlayerActions.Look);
                InputAction jump = FindOptional(clonedGameplay, PlayerActions.Jump);
                InputAction attack = FindOptional(clonedGameplay, PlayerActions.Attack);
                InputAction sprint = FindOptional(clonedGameplay, PlayerActions.Sprint);

                InputActionMap clonedUi = string.IsNullOrWhiteSpace(uiMapName)
                    ? null
                    : clone.FindActionMap(uiMapName);
                InputAction navigate = FindOptional(clonedUi, UiActions.Navigate);
                InputAction cancel = FindOptional(clonedUi, UiActions.Cancel);

                RequireType(move, InputActionType.Value);
                RequireType(look, InputActionType.Value);
                RequireType(navigate, InputActionType.Value);
                RequireType(jump, InputActionType.Button);
                RequireType(attack, InputActionType.Button);
                RequireType(sprint, InputActionType.Button);
                RequireType(cancel, InputActionType.Button);

                runtimeAsset = clone;
                gameplayMap = clonedGameplay;
                uiMap = clonedUi;
                uiNavigate = navigate;
                keepUiMapEnabled = keepUi && clonedUi != null;
                gameplayEnabled = true;
                player = new PlayerControls(
                    move,
                    look,
                    jump,
                    attack,
                    sprint,
                    lookSensitivity);
                ui = new UiControls(navigate, cancel);
                UiLocks.Clear();
                running = true;
                ApplyMaps();
            }
            catch
            {
                ClearRuntimeState();
                DestroyObject(clone);
                throw;
            }
        }

        static InputAction FindOptional(InputActionMap map, string actionName)
        {
            if (map == null)
            {
                return null;
            }

            InputAction action = map.FindAction(actionName);
            if (action == null)
            {
                Debug.LogWarning(
                    $"[GameInput] Map '{map.name}' 未配置动作 '{actionName}'，" +
                    "对应轮询将返回默认值。");
            }

            return action;
        }

        static void RequireType(InputAction action, InputActionType expected)
        {
            if (action == null || action.type == expected)
            {
                return;
            }

            throw new InputStateException(
                $"动作 '{action.name}' 必须是 {expected}，当前是 {action.type}。");
        }

        static void ApplyMaps()
        {
            bool uiLocked = UiLocks.Count > 0;
            SetEnabled(gameplayMap, gameplayEnabled && !uiLocked);
            bool enableUi = keepUiMapEnabled || uiLocked;
            SetEnabled(uiMap, enableUi);
            // 玩法期间保留 UI Map（HUD / Cancel），关掉 Navigate，避免与 Move 抢 WASD / 左摇杆。
            SetEnabled(uiNavigate, enableUi && uiLocked);
        }

        static void SetEnabled(InputActionMap map, bool enabled)
        {
            if (map == null || map.enabled == enabled)
            {
                return;
            }

            if (enabled)
            {
                map.Enable();
            }
            else
            {
                map.Disable();
            }
        }

        static void SetEnabled(InputAction action, bool enabled)
        {
            if (action == null || action.enabled == enabled)
            {
                return;
            }

            if (enabled)
            {
                action.Enable();
            }
            else
            {
                action.Disable();
            }
        }

        static void ClearRuntimeState()
        {
            running = false;
            runtimeAsset = null;
            gameplayMap = null;
            uiMap = null;
            uiNavigate = null;
            player = null;
            ui = null;
            keepUiMapEnabled = false;
            gameplayEnabled = true;
            UiLocks.Clear();
        }

        static void RequireRunning(string api)
        {
            if (!running)
            {
                throw new InputStateException(
                    $"GameInput.{api} 要求先调用 GameInput.Init。");
            }
        }

        static void RequireMainThread(string api)
        {
            if (mainThreadId != 0 &&
                Thread.CurrentThread.ManagedThreadId != mainThreadId)
            {
                throw new InputStateException(
                    $"GameInput.{api} 只能在 Unity 主线程调用。");
            }
        }

        static void DestroyObject(UnityEngine.Object target)
        {
            if (target == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(target);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }
    }
}
