using GestureSign.Common.Configuration;
using GestureSign.Common.Input;
using GestureSign.Common.InterProcessCommunication;
using ManagedWinapi.Hooks;
using Microsoft.Win32;
using System;
using System.Threading.Tasks;

namespace GestureSign.Daemon.Input
{
    internal class InputProvider : IDisposable
    {
        private bool disposedValue = false; // To detect redundant calls
        private MessageWindow _messageWindow;
        private CustomNamedPipeServer _deviceStateServer;
        private int _stateUpdating;

        public LowLevelMouseHook LowLevelMouseHook;
        public LowLevelKeyboardHook LowLevelKeyboardHook;
        public GestureBindingMatcher BindingMatcher;
        public event RawPointsDataMessageEventHandler PointsIntercepted;

        public InputProvider()
        {
            _messageWindow = new MessageWindow();
            _messageWindow.PointsIntercepted += MessageWindow_PointsIntercepted;

            AppConfig.ConfigChanged += AppConfig_ConfigChanged;

            LowLevelMouseHook = new LowLevelMouseHook();
            LowLevelKeyboardHook = new LowLevelKeyboardHook();
            BindingMatcher = new GestureBindingMatcher();
            BindingMatcher.UpdateBinding(AppConfig.DrawingBinding);

            // The keyboard hook feeds modifier and main-key events into the matcher. It's installed
            // whenever the binding requires it (keyboard bindings always, mouse bindings with modifiers).
            LowLevelKeyboardHook.KeyIntercepted += OnKeyboardKeyIntercepted;

            var binding = AppConfig.DrawingBinding;
            if (!binding.IsEmpty)
                Task.Delay(1000).ContinueWith((t) =>
                {
                    LowLevelMouseHook.StartHook();
                    if (BindingMatcher.BindingRequiresKeyboardHook)
                        LowLevelKeyboardHook.StartHook();
                }, TaskScheduler.FromCurrentSynchronizationContext());


            SystemEvents.SessionSwitch += new SessionSwitchEventHandler(OnSessionSwitch);
            SystemEvents.PowerModeChanged += new PowerModeChangedEventHandler(OnPowerModeChanged);

            _deviceStateServer = new CustomNamedPipeServer(Common.Constants.Daemon + "DeviceState", IpcCommands.SynDeviceState,
                () => HidDevice.EnumerateDevices());
        }

        /// <summary>
        /// Forwards keyboard hook events to the matcher. The matcher decides whether to swallow
        /// the event (only for a matching main key); modifier keys always pass through.
        /// </summary>
        private void OnKeyboardKeyIntercepted(int msg, int vkCode, int scanCode, int flags, int time, IntPtr dwExtraInfo, ref bool handled)
        {
            // WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104, WM_KEYUP = 0x101, WM_SYSKEYUP = 0x105.
            bool isDown = msg == 0x100 || msg == 0x104;
            bool isUp = msg == 0x101 || msg == 0x105;
            if (!isDown && !isUp) return;

            bool swallow = BindingMatcher.OnKeyboardKey(vkCode, isDown);
            if (swallow)
                handled = true;
        }

        private void AppConfig_ConfigChanged(object sender, System.EventArgs e)
        {
            var binding = AppConfig.DrawingBinding;
            BindingMatcher.UpdateBinding(binding);

            if (!binding.IsEmpty)
                LowLevelMouseHook.StartHook();
            else
                LowLevelMouseHook.Unhook();

            if (BindingMatcher.BindingRequiresKeyboardHook)
                LowLevelKeyboardHook.StartHook();
            else
                LowLevelKeyboardHook.Unhook();

            UpdateDeviceState();
        }

        private void MessageWindow_PointsIntercepted(object sender, RawPointsDataMessageEventArgs e)
        {
            if (e.RawData.Count == 0)
                return;
            PointsIntercepted?.Invoke(this, e);
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                UpdateDeviceState();
            }
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            // We need to handle sleeping(and other related events)
            // This is so we never lose the lock on the touchpad hardware.
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLogon:
                case SessionSwitchReason.SessionUnlock:
                    UpdateDeviceState();
                    break;
                default:
                    break;
            }
        }

        private void UpdateDeviceState()
        {
            if (0 == System.Threading.Interlocked.Exchange(ref _stateUpdating, 1))
            {
                Task.Delay(600).ContinueWith((t) =>
                {
                    System.Threading.Interlocked.Exchange(ref _stateUpdating, 0);
                    _messageWindow.UpdateRegistration();
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
        }

        #region IDisposable Support

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    AppConfig.ConfigChanged -= AppConfig_ConfigChanged;
                }

                SystemEvents.SessionSwitch -= OnSessionSwitch;
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                LowLevelMouseHook?.Unhook();
                LowLevelKeyboardHook?.Unhook();
                _deviceStateServer.Dispose();
                disposedValue = true;
            }
        }

        ~InputProvider()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
