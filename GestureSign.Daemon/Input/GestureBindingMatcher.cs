using System;
using GestureSign.Common.Input;
using ManagedWinapi.Hooks;

namespace GestureSign.Daemon.Input
{
    /// <summary>
    /// Runtime state machine that decides whether the user-configured <see cref="GestureBinding"/>
    /// is currently "fully held" — i.e. the main key/button is down AND the exact set of required
    /// modifier keys is currently pressed.
    ///
    /// Owned by <see cref="InputProvider"/>. Hooked into both the low-level mouse and keyboard hooks;
    /// <see cref="OnMouseButton"/> / <see cref="OnKeyboardKey"/> return <c>true</c> if the event
    /// should be swallowed (blocked from the foreground app).
    ///
    /// Key design choices:
    ///   • Modifier equality is EXACT, not subset. "Ctrl+Space" will NOT activate on
    ///     "Ctrl+Shift+Space" — users depending on Shift-prefixed bindings elsewhere are protected.
    ///   • Left and right modifier keys are tracked independently (via separate flags internally)
    ///     but normalized to the same <see cref="GestureModifiers"/> bit for matching purposes:
    ///     LeftCtrl and RightCtrl both satisfy a Ctrl requirement.
    ///   • Modifier keys are NEVER swallowed. Ctrl/Shift/Alt/Win are used by other apps and by
    ///     the binding's own modifier state, so we watch them but pass them through unchanged.
    ///   • The main key/button is swallowed only when the full binding becomes active.
    ///   • <see cref="Suspend"/> is a kill switch used by the IPC pause mechanism while the
    ///     UI's BindingCaptureBox is in capture mode, so the user can press the currently-bound
    ///     keys to change the binding without triggering a stray gesture draw.
    /// </summary>
    internal sealed class GestureBindingMatcher
    {
        // --- Configuration ---
        private GestureBinding _binding = GestureBinding.None;

        // --- Per-modifier-physical-key tracking ---
        // We track left and right variants independently because the hook delivers them as
        // separate VK codes. For matching, both count as the same modifier bit.
        private bool _leftCtrlDown, _rightCtrlDown;
        private bool _leftShiftDown, _rightShiftDown;
        private bool _leftAltDown, _rightAltDown;
        private bool _leftWinDown, _rightWinDown;

        // --- Main-key state ---
        private bool _mainKeyHeld;

        // --- Derived state ---
        private bool _isActive;

        /// <summary>
        /// Fires when the binding transitions from "not fully held" to "fully held".
        /// Subscribed to by <see cref="PointEventTranslator"/> to start a gesture draw.
        /// </summary>
        public event Action Activated;

        /// <summary>
        /// Fires when the binding transitions from "fully held" to "not fully held" (either the
        /// main key was released or a required modifier was released). Subscribed to by
        /// <see cref="PointEventTranslator"/> to end a gesture draw.
        /// </summary>
        public event Action Deactivated;

        /// <summary>
        /// True while the user has the binding fully held down. Used by other components that
        /// need to know whether a draw is in progress.
        /// </summary>
        public bool IsActive => _isActive;

        /// <summary>
        /// When true, the matcher will not activate regardless of input state. Used by the
        /// IPC pause mechanism while the UI capture control is actively reading input.
        /// </summary>
        public bool Suspend { get; set; }

        /// <summary>
        /// True if the configured binding is a keyboard key (not a mouse button). Used by
        /// InputProvider to decide whether to start the keyboard hook.
        /// </summary>
        public bool BindingRequiresKeyboardHook
        {
            get
            {
                if (_binding.IsEmpty) return false;
                // Keyboard bindings always need the keyboard hook. Mouse bindings with modifiers
                // also need it to track modifier state.
                return _binding.Kind == GestureBindingKind.KeyboardKey
                       || _binding.Modifiers != GestureModifiers.None;
            }
        }

        /// <summary>
        /// Replace the current binding. Resets all held-state tracking so a stale
        /// "main key held" from the old binding can't ghost-activate the new one.
        /// </summary>
        public void UpdateBinding(GestureBinding newBinding)
        {
            _binding = newBinding ?? GestureBinding.None;
            _mainKeyHeld = false;
            // Deliberately do NOT reset modifier state: modifier keys are physically held or
            // not regardless of which binding we have. The next hook event will refresh them
            // anyway, but resetting would briefly lie about the real physical state.
            RecomputeActive();
        }

        /// <summary>
        /// Called from the mouse hook callback for every mouse button event. Returns <c>true</c>
        /// if the event should be swallowed (blocked from the foreground application).
        /// </summary>
        public bool OnMouseButton(MouseActions button, bool isDown)
        {
            if (_binding.IsEmpty || Suspend) return false;
            if (_binding.Kind != GestureBindingKind.MouseButton) return false;
            if ((MouseActions)_binding.MainCode != button) return false;

            _mainKeyHeld = isDown;
            var wasActive = _isActive;
            RecomputeActive();

            // Swallow the mouse event ONLY if the full binding (including modifiers) matched.
            // If the user pressed the bound button without the required modifiers, the click
            // passes through unchanged.
            return isDown ? _isActive : wasActive;
        }

        /// <summary>
        /// Called from the keyboard hook callback for every key event. Returns <c>true</c>
        /// if the event should be swallowed. Modifier keys are always passed through
        /// (returns false) but still update the internal modifier state.
        /// </summary>
        public bool OnKeyboardKey(int vkCode, bool isDown)
        {
            // Always update modifier state, regardless of binding suspension or emptiness —
            // users might release a modifier while suspended and we'd lose track otherwise.
            if (TryUpdateModifierState(vkCode, isDown))
            {
                RecomputeActive();
                return false; // modifiers are never swallowed
            }

            if (_binding.IsEmpty || Suspend) return false;
            if (_binding.Kind != GestureBindingKind.KeyboardKey) return false;
            if (_binding.MainCode != vkCode) return false;

            _mainKeyHeld = isDown;
            var wasActive = _isActive;
            RecomputeActive();

            return isDown ? _isActive : wasActive;
        }

        /// <summary>
        /// If <paramref name="vkCode"/> is a modifier key, update the corresponding left/right
        /// flag and return <c>true</c>. Otherwise return <c>false</c> without modifying state.
        /// </summary>
        private bool TryUpdateModifierState(int vkCode, bool isDown)
        {
            switch (vkCode)
            {
                // VK_CONTROL (0x11) is only fired for unqualified Ctrl; Ctrl from the hook
                // usually comes through as VK_LCONTROL (0xA2) or VK_RCONTROL (0xA3).
                case 0xA2: _leftCtrlDown  = isDown; return true;
                case 0xA3: _rightCtrlDown = isDown; return true;
                case 0x11: _leftCtrlDown  = isDown; return true; // best-effort fallback

                case 0xA0: _leftShiftDown  = isDown; return true;
                case 0xA1: _rightShiftDown = isDown; return true;
                case 0x10: _leftShiftDown  = isDown; return true;

                case 0xA4: _leftAltDown  = isDown; return true;
                case 0xA5: _rightAltDown = isDown; return true;
                case 0x12: _leftAltDown  = isDown; return true;

                case 0x5B: _leftWinDown  = isDown; return true;
                case 0x5C: _rightWinDown = isDown; return true;

                default: return false;
            }
        }

        private GestureModifiers CurrentModifiers()
        {
            GestureModifiers m = GestureModifiers.None;
            if (_leftCtrlDown  || _rightCtrlDown)  m |= GestureModifiers.Ctrl;
            if (_leftShiftDown || _rightShiftDown) m |= GestureModifiers.Shift;
            if (_leftAltDown   || _rightAltDown)   m |= GestureModifiers.Alt;
            if (_leftWinDown   || _rightWinDown)   m |= GestureModifiers.Win;
            return m;
        }

        private void RecomputeActive()
        {
            bool wasActive = _isActive;
            // Exact modifier equality — see class-level comment.
            _isActive = _mainKeyHeld
                        && !Suspend
                        && !_binding.IsEmpty
                        && CurrentModifiers() == _binding.Modifiers;

            if (_isActive && !wasActive)
                Activated?.Invoke();
            else if (!_isActive && wasActive)
                Deactivated?.Invoke();
        }
    }
}
