using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GestureSign.Common.Input;
using ManagedWinapi.Hooks;

namespace GestureSign.ControlPanel.UserControls
{
    /// <summary>
    /// Press-to-capture binding editor. Click "Change", press any key or mouse button (with
    /// optional Ctrl/Shift/Alt/Win modifiers), and the control records and saves the resulting
    /// <see cref="GestureBinding"/>.
    ///
    /// Unlike a hotkey-focused control (e.g. MahApps HotKeyBox), this accepts:
    ///   - solo keyboard keys (e.g. F13)
    ///   - solo mouse buttons (Right, Middle, X1, X2, Left)
    ///   - modifier+key chords (Ctrl+Shift+Space)
    ///   - modifier+mouse chords (Ctrl+MiddleClick)
    ///
    /// Modifier-only combinations never finalize — the user must press a main key or button
    /// to commit. Escape cancels without changing anything; losing focus or a 10-second idle
    /// timeout also cancel.
    ///
    /// While in capture mode, the control raises <see cref="CaptureStarted"/> so the hosting
    /// page can send a Pause IPC to the daemon, preventing the daemon's global hook from
    /// firing a stray gesture on the very keys the user is trying to bind.
    /// </summary>
    public partial class BindingCaptureBox : UserControl
    {
        #region Dependency properties

        public static readonly DependencyProperty BindingProperty =
            DependencyProperty.Register(
                nameof(Binding),
                typeof(GestureBinding),
                typeof(BindingCaptureBox),
                new FrameworkPropertyMetadata(
                    GestureBinding.None,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnBindingChanged));

        public GestureBinding Binding
        {
            get { return (GestureBinding)GetValue(BindingProperty); }
            set { SetValue(BindingProperty, value); }
        }

        private static void OnBindingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (BindingCaptureBox)d;
            ctrl.RefreshIdleText();
        }

        #endregion

        #region Events

        public event EventHandler CaptureStarted;
        public event EventHandler CaptureEnded;

        #endregion

        private bool _isCapturing;
        private GestureModifiers _capturedModifiers;
        private DispatcherTimer _timeoutTimer;

        public BindingCaptureBox()
        {
            InitializeComponent();
            Loaded += (s, e) => RefreshIdleText();
        }

        private void RefreshIdleText()
        {
            var binding = Binding ?? GestureBinding.None;
            if (CurrentBindingValueText != null)
                CurrentBindingValueText.Text = binding.GetDisplayString();
        }

        #region Capture mode entry/exit

        private void ChangeButton_Click(object sender, RoutedEventArgs e)
        {
            EnterCaptureMode();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            Binding = GestureBinding.None;
        }

        private void EnterCaptureMode()
        {
            if (_isCapturing) return;
            _isCapturing = true;
            _capturedModifiers = GestureModifiers.None;

            IdlePanel.Visibility = Visibility.Collapsed;
            CapturingPanel.Visibility = Visibility.Visible;
            OuterBorder.BorderBrush = (Brush)FindResource("AccentColorBrush");
            OuterBorder.BorderThickness = new Thickness(2);

            ChangeButton.IsEnabled = false;
            ClearButton.IsEnabled = false;

            // Grab keyboard focus so PreviewKeyDown fires inside the UserControl.
            Focus();
            Keyboard.Focus(this);

            // Install handlers while in capture mode; removed on exit so the control doesn't
            // eat keys or clicks when idle.
            PreviewKeyDown += CaptureBox_PreviewKeyDown;
            PreviewKeyUp += CaptureBox_PreviewKeyUp;
            PreviewMouseDown += CaptureBox_PreviewMouseDown;
            LostKeyboardFocus += CaptureBox_LostKeyboardFocus;

            _timeoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _timeoutTimer.Tick += (s, e) => ExitCaptureMode(commit: false);
            _timeoutTimer.Start();

            CaptureStarted?.Invoke(this, EventArgs.Empty);
        }

        private void ExitCaptureMode(bool commit)
        {
            if (!_isCapturing) return;
            _isCapturing = false;

            PreviewKeyDown -= CaptureBox_PreviewKeyDown;
            PreviewKeyUp -= CaptureBox_PreviewKeyUp;
            PreviewMouseDown -= CaptureBox_PreviewMouseDown;
            LostKeyboardFocus -= CaptureBox_LostKeyboardFocus;

            _timeoutTimer?.Stop();
            _timeoutTimer = null;

            IdlePanel.Visibility = Visibility.Visible;
            CapturingPanel.Visibility = Visibility.Collapsed;
            OuterBorder.BorderBrush = (Brush)FindResource("AccentColorBrush3");
            OuterBorder.BorderThickness = new Thickness(1);
            ChangeButton.IsEnabled = true;
            ClearButton.IsEnabled = true;

            RefreshIdleText();
            CaptureEnded?.Invoke(this, EventArgs.Empty);
        }

        #endregion

        #region Capture input handlers

        private void CaptureBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Escape cancels capture without changing the binding.
            if (e.Key == Key.Escape)
            {
                ExitCaptureMode(commit: false);
                e.Handled = true;
                return;
            }

            var effectiveKey = e.Key == Key.System ? e.SystemKey : e.Key;
            GestureModifiers asModifier;
            if (TryClassifyModifier(effectiveKey, out asModifier))
            {
                // Modifier keys accumulate but do not finalize the binding.
                _capturedModifiers |= asModifier;
                UpdateCapturePromptWithModifiers();
                e.Handled = true;
                return;
            }

            // Non-modifier key: finalize as a keyboard binding.
            int vk = KeyInterop.VirtualKeyFromKey(effectiveKey);
            if (vk == 0)
            {
                e.Handled = true;
                return;
            }

            var newBinding = new GestureBinding
            {
                Kind = GestureBindingKind.KeyboardKey,
                MainCode = vk,
                Modifiers = _capturedModifiers,
            };
            Binding = newBinding;
            ExitCaptureMode(commit: true);
            e.Handled = true;
        }

        private void CaptureBox_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            // Let modifier key releases update the live display so the user can see their
            // intermediate state, but never finalize on a key-up.
            var effectiveKey = e.Key == Key.System ? e.SystemKey : e.Key;
            GestureModifiers asModifier;
            if (TryClassifyModifier(effectiveKey, out asModifier))
            {
                _capturedModifiers &= ~asModifier;
                UpdateCapturePromptWithModifiers();
                e.Handled = true;
            }
        }

        private void CaptureBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            MouseActions? action = null;
            switch (e.ChangedButton)
            {
                case MouseButton.Left:
                    // Left click on the control itself is used to focus, not bind — the user
                    // expects clicking the control to work normally. Only bind Left if the
                    // click happens OUTSIDE our Change/Clear buttons.
                    if (IsClickOnOurButtons(e)) return;
                    action = MouseActions.Left; break;
                case MouseButton.Right:
                    action = MouseActions.Right; break;
                case MouseButton.Middle:
                    action = MouseActions.Middle; break;
                case MouseButton.XButton1:
                    action = MouseActions.XButton1; break;
                case MouseButton.XButton2:
                    action = MouseActions.XButton2; break;
            }

            if (!action.HasValue) return;

            var newBinding = new GestureBinding
            {
                Kind = GestureBindingKind.MouseButton,
                MainCode = (int)action.Value,
                Modifiers = _capturedModifiers,
            };
            Binding = newBinding;
            ExitCaptureMode(commit: true);
            e.Handled = true;
        }

        private bool IsClickOnOurButtons(MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            while (source != null)
            {
                if (source == ChangeButton || source == ClearButton) return true;
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        private void CaptureBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            ExitCaptureMode(commit: false);
        }

        private void UpdateCapturePromptWithModifiers()
        {
            var prompt = "Press any key or mouse button...";
            if (_capturedModifiers != GestureModifiers.None)
            {
                var prefix = "";
                if ((_capturedModifiers & GestureModifiers.Ctrl)  != 0) prefix += "Ctrl+";
                if ((_capturedModifiers & GestureModifiers.Shift) != 0) prefix += "Shift+";
                if ((_capturedModifiers & GestureModifiers.Alt)   != 0) prefix += "Alt+";
                if ((_capturedModifiers & GestureModifiers.Win)   != 0) prefix += "Win+";
                prompt = prefix + "(press main key or button)";
            }
            if (CapturePromptText != null)
                CapturePromptText.Text = prompt;
        }

        #endregion

        #region Modifier classification

        /// <summary>
        /// Maps a WPF <see cref="Key"/> to our <see cref="GestureModifiers"/> enum, handling
        /// both left and right variants as the same modifier bit.
        /// </summary>
        private static bool TryClassifyModifier(Key key, out GestureModifiers modifier)
        {
            switch (key)
            {
                case Key.LeftCtrl:
                case Key.RightCtrl:
                    modifier = GestureModifiers.Ctrl; return true;
                case Key.LeftShift:
                case Key.RightShift:
                    modifier = GestureModifiers.Shift; return true;
                case Key.LeftAlt:
                case Key.RightAlt:
                    modifier = GestureModifiers.Alt; return true;
                case Key.LWin:
                case Key.RWin:
                    modifier = GestureModifiers.Win; return true;
                default:
                    modifier = GestureModifiers.None; return false;
            }
        }

        #endregion
    }
}
