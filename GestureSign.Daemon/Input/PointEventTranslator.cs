using System;
using System.Collections.Generic;
using System.Linq;
using GestureSign.Common.Configuration;
using GestureSign.Common.Input;
using ManagedWinapi.Hooks;

namespace GestureSign.Daemon.Input
{
    public class PointEventTranslator
    {
        private int _lastPointsCount;
        private HashSet<MouseActions> _pressedMouseButton;
        private readonly GestureBindingMatcher _bindingMatcher;

        // Position of the last mouse event, cached so keyboard-triggered draws know where to
        // synthesize the "starting point" from. The raw hook provides mouse coords on every
        // mouse event but the user's gesture drawing path needs a starting point even when
        // the trigger is keyboard-based (no initial mouse-down to supply one).
        private System.Drawing.Point _lastMousePoint;

        internal Devices SourceDevice { get; private set; }

        internal PointEventTranslator(InputProvider inputProvider)
        {
            _pressedMouseButton = new HashSet<MouseActions>();
            _bindingMatcher = inputProvider.BindingMatcher;
            inputProvider.PointsIntercepted += TranslateTouchEvent;
            inputProvider.LowLevelMouseHook.MouseDown += LowLevelMouseHook_MouseDown;
            inputProvider.LowLevelMouseHook.MouseMove += LowLevelMouseHook_MouseMove;
            inputProvider.LowLevelMouseHook.MouseUp += LowLevelMouseHook_MouseUp;

            // Keyboard-trigger path: when a keyboard binding activates, start a draw at the
            // current cursor position; when it deactivates, end the draw. Mouse-trigger path
            // is handled inline in the MouseDown/MouseUp callbacks below (they still need to
            // call the matcher, which in turn fires these same events for mouse bindings).
            _bindingMatcher.Activated += OnBindingActivated;
            _bindingMatcher.Deactivated += OnBindingDeactivated;
        }

        private void OnBindingActivated()
        {
            // Only fire the keyboard-triggered OnPointDown here for keyboard bindings. For
            // mouse bindings, the MouseDown handler already synthesized the InputPointsEventArgs
            // with the correct coordinates before the matcher fired its Activated event.
            if (_bindingMatcher == null) return;
            if (_pressedMouseButton.Count > 0) return;

            // Check if the active binding is a mouse binding; if so, the mouse callback
            // already dispatched the point-down event.
            var binding = GestureSign.Common.Configuration.AppConfig.DrawingBinding;
            if (binding.Kind != GestureBindingKind.KeyboardKey) return;

            var args = new InputPointsEventArgs(new List<InputPoint>(new[] { new InputPoint(1, _lastMousePoint) }), Devices.Mouse);
            OnPointDown(args);
        }

        private void OnBindingDeactivated()
        {
            if (_bindingMatcher == null) return;

            var binding = GestureSign.Common.Configuration.AppConfig.DrawingBinding;
            if (binding.Kind != GestureBindingKind.KeyboardKey) return;

            var args = new InputPointsEventArgs(new List<InputPoint>(new[] { new InputPoint(1, _lastMousePoint) }), Devices.Mouse);
            OnPointUp(args);
        }

        #region Custom Events

        public event EventHandler<InputPointsEventArgs> PointDown;

        protected virtual void OnPointDown(InputPointsEventArgs args)
        {
            if (SourceDevice != Devices.None && SourceDevice != args.PointSource && args.PointSource != Devices.Pen) return;
            SourceDevice = args.PointSource;
            PointDown?.Invoke(this, args);
        }

        public event EventHandler<InputPointsEventArgs> PointUp;

        protected virtual void OnPointUp(InputPointsEventArgs args)
        {
            if (SourceDevice != Devices.None && SourceDevice != args.PointSource) return;

            PointUp?.Invoke(this, args);

            SourceDevice = Devices.None;
        }

        public event EventHandler<InputPointsEventArgs> PointMove;

        protected virtual void OnPointMove(InputPointsEventArgs args)
        {
            if (SourceDevice != args.PointSource) return;
            PointMove?.Invoke(this, args);
        }

        #endregion

        #region Private Methods

        private void LowLevelMouseHook_MouseUp(LowLevelMouseMessage mouseMessage, ref bool handled)
        {
            _lastMousePoint = mouseMessage.Point;
            var button = (MouseActions)mouseMessage.Button;

            // Feed the matcher so modifier tracking and state bookkeeping stay in sync.
            // The matcher returns true when this event was the "main key" of an active binding
            // and the event should be swallowed. For mouse-up, "active before" means we were
            // drawing; the matcher will flip _isActive to false on this call and return true.
            bool shouldSwallow = _bindingMatcher != null
                                 && _bindingMatcher.OnMouseButton(button, isDown: false);

            if (shouldSwallow)
            {
                var args = new InputPointsEventArgs(new List<InputPoint>(new[] { new InputPoint(1, mouseMessage.Point) }), Devices.Mouse);
                OnPointUp(args);
                handled = args.Handled;
            }
            _pressedMouseButton.Remove(button);
        }

        private void LowLevelMouseHook_MouseMove(LowLevelMouseMessage mouseMessage, ref bool handled)
        {
            _lastMousePoint = mouseMessage.Point;
            var args = new InputPointsEventArgs(new List<InputPoint>(new[] { new InputPoint(1, mouseMessage.Point) }), Devices.Mouse);
            OnPointMove(args);
        }

        private void LowLevelMouseHook_MouseDown(LowLevelMouseMessage mouseMessage, ref bool handled)
        {
            _lastMousePoint = mouseMessage.Point;
            var button = (MouseActions)mouseMessage.Button;

            // Feed the matcher. It updates modifier state internally (for mouse bindings with
            // modifier requirements like "Ctrl+MiddleClick") and returns true if this press
            // activated the full binding.
            bool shouldSwallow = _bindingMatcher != null
                                 && _bindingMatcher.OnMouseButton(button, isDown: true)
                                 && _pressedMouseButton.Count == 0;

            if (shouldSwallow)
            {
                var args = new InputPointsEventArgs(new List<InputPoint>(new[] { new InputPoint(1, mouseMessage.Point) }), Devices.Mouse);
                OnPointDown(args);
                handled = args.Handled;
            }
            _pressedMouseButton.Add(button);
        }

        private void TranslateTouchEvent(object sender, RawPointsDataMessageEventArgs e)
        {
            if ((e.SourceDevice & Devices.TouchDevice) != 0)
            {
                int releaseCount = e.RawData.Count(rtd => rtd.State == 0);

                if (e.RawData.Count == _lastPointsCount)
                {
                    if (releaseCount != 0)
                    {
                        OnPointUp(new InputPointsEventArgs(e.RawData, e.SourceDevice));
                        _lastPointsCount -= releaseCount;
                        return;
                    }
                    OnPointMove(new InputPointsEventArgs(e.RawData, e.SourceDevice));
                }
                else if (e.RawData.Count > _lastPointsCount)
                {
                    if (releaseCount != 0)
                        return;
                    if (PointCapture.Instance.InputPoints.Any(p => p.Count > 10))
                    {
                        OnPointMove(new InputPointsEventArgs(e.RawData, e.SourceDevice));
                        return;
                    }
                    _lastPointsCount = e.RawData.Count;
                    OnPointDown(new InputPointsEventArgs(e.RawData, e.SourceDevice));
                }
                else
                {
                    OnPointUp(new InputPointsEventArgs(e.RawData, e.SourceDevice));
                    _lastPointsCount = _lastPointsCount - e.RawData.Count > releaseCount ? e.RawData.Count : _lastPointsCount - releaseCount;
                }
            }
            else if (e.SourceDevice == Devices.Pen)
            {
                bool release = (e.RawData[0].State & (DeviceStates.Invert | DeviceStates.RightClickButton)) == 0 || (e.RawData[0].State & DeviceStates.InRange) == 0;
                bool tip = (e.RawData[0].State & (DeviceStates.Eraser | DeviceStates.Tip)) != 0;

                if (release)
                {
                    OnPointUp(new InputPointsEventArgs(e.RawData, e.SourceDevice));
                    _lastPointsCount = 0;
                    return;
                }

                var penSetting = AppConfig.PenGestureButton;
                bool drawByTip = (penSetting & DeviceStates.Tip) != 0;
                bool drawByHover = (penSetting & DeviceStates.InRange) != 0;

                if (drawByHover && drawByTip)
                {
                    if (_lastPointsCount == 1 && SourceDevice == Devices.Pen)
                    {
                        OnPointMove(new InputPointsEventArgs(e.RawData, e.SourceDevice));
                    }
                    else if (_lastPointsCount >= 0)
                    {
                        _lastPointsCount = 1;
                        OnPointDown(new InputPointsEventArgs(e.RawData, e.SourceDevice));
                    }
                }
                else if (drawByTip)
                {
                    if (!tip)
                    {
                        if (SourceDevice == Devices.Pen)
                        {
                            OnPointUp(new InputPointsEventArgs(e.RawData, e.SourceDevice));
                            _lastPointsCount = 0;
                        }
                        return;
                    }

                    if (_lastPointsCount == 1 && SourceDevice == Devices.Pen)
                    {
                        OnPointMove(new InputPointsEventArgs(e.RawData, e.SourceDevice));
                    }
                    else if (_lastPointsCount >= 0)
                    {
                        _lastPointsCount = 1;
                        OnPointDown(new InputPointsEventArgs(e.RawData, e.SourceDevice));
                    }
                }
                else if (drawByHover)
                {
                    if (_lastPointsCount == 1 && SourceDevice == Devices.Pen)
                    {
                        if (tip)
                        {
                            OnPointDown(new InputPointsEventArgs(e.RawData, e.SourceDevice));
                            _lastPointsCount = -1;
                        }
                        else
                        {
                            OnPointMove(new InputPointsEventArgs(e.RawData, e.SourceDevice));
                        }
                    }
                    else if (_lastPointsCount >= 0)
                    {
                        if (tip)
                        {
                            _lastPointsCount = -1;
                            return;
                        }
                        _lastPointsCount = 1;
                        OnPointDown(new InputPointsEventArgs(e.RawData, e.SourceDevice));
                    }
                }
            }
        }

        #endregion
    }
}
