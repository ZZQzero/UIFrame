using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Input
{
    [CreateAssetMenu(
        menuName = "Game/Input Runtime Config",
        fileName = "InputRuntimeConfig",
        order = 20)]
    public sealed class InputRuntimeConfig : ScriptableObject
    {
        [SerializeField] InputActionAsset actions;
        [SerializeField] string gameplayMap = "Player";
        [SerializeField] string uiMap = "UI";
        [SerializeField, Tooltip(
            "玩法期间是否保持 UI Map 开启（HUD 点击 / Cancel）。" +
            "Navigate 仅在 PushUi 之后启用，避免与 Move 抢 WASD。")]
        bool keepUiMapEnabled = true;
        [SerializeField, Min(0.01f)] float lookSensitivity = 1f;

        public InputActionAsset Actions => actions;
        public string GameplayMap => gameplayMap;
        public string UiMap => uiMap;
        public bool KeepUiMapEnabled => keepUiMapEnabled;
        public float LookSensitivity => lookSensitivity;

        public static InputRuntimeConfig Create(
            InputActionAsset actions,
            string gameplayMap = "Player",
            string uiMap = "UI",
            bool keepUiMapEnabled = true,
            float lookSensitivity = 1f)
        {
            var config = CreateInstance<InputRuntimeConfig>();
            config.actions = actions;
            config.gameplayMap = gameplayMap;
            config.uiMap = uiMap;
            config.keepUiMapEnabled = keepUiMapEnabled;
            config.lookSensitivity = lookSensitivity;
            return config;
        }

        public void Validate()
        {
            GameInput.ValidateAsset(
                actions,
                gameplayMap,
                uiMap,
                lookSensitivity);
        }
    }
}
