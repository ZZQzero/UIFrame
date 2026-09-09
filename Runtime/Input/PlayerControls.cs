using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Input
{
    /// <summary>
    /// 玩法轮询入口。Init 时缓存 InputAction 引用，Update 里只 ReadValue / WasPressedThisFrame。
    /// Move 在 Init 时必须存在；其余动作为 null 时返回 default。
    /// 不要跨 Shutdown 缓存本对象；每帧从 <see cref="GameInput.Player"/> 读取。
    /// </summary>
    public sealed class PlayerControls
    {
        readonly InputAction move;
        readonly InputAction look;
        readonly InputAction jump;
        readonly InputAction attack;
        readonly InputAction sprint;
        float lookSensitivity;

        internal PlayerControls(
            InputAction move,
            InputAction look,
            InputAction jump,
            InputAction attack,
            InputAction sprint,
            float lookSensitivity)
        {
            this.move = move ?? throw new ArgumentNullException(nameof(move));
            this.look = look;
            this.jump = jump;
            this.attack = attack;
            this.sprint = sprint;
            this.lookSensitivity = lookSensitivity;
        }

        public Vector2 Move => move.ReadValue<Vector2>();

        public Vector2 Look =>
            look == null
                ? default
                : look.ReadValue<Vector2>() * lookSensitivity;

        public bool JumpPressed =>
            jump != null && jump.WasPressedThisFrame();

        public bool AttackPressed =>
            attack != null && attack.WasPressedThisFrame();

        public bool SprintHeld =>
            sprint != null && sprint.IsPressed();

        internal float LookSensitivity
        {
            get => lookSensitivity;
            set => lookSensitivity = value;
        }
    }

    /// <summary>
    /// UI Map 轮询。Cancel / Navigate 未配置时返回 default。
    /// 不要跨 Shutdown 缓存本对象；每帧从 <see cref="GameInput.UI"/> 读取。
    /// Navigate 仅在 UI 锁非空时启用。
    /// </summary>
    public sealed class UiControls
    {
        readonly InputAction navigate;
        readonly InputAction cancel;

        internal UiControls(InputAction navigate, InputAction cancel)
        {
            this.navigate = navigate;
            this.cancel = cancel;
        }

        public Vector2 Navigate =>
            navigate == null ? default : navigate.ReadValue<Vector2>();

        public bool CancelPressed =>
            cancel != null && cancel.WasPressedThisFrame();
    }
}
