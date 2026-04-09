using System;
using System.Text;
using ManagedWinapi.Hooks;

namespace GestureSign.Common.Input
{
    /// <summary>
    /// Discriminates whether a binding's "main" input is a mouse button or a keyboard key.
    /// </summary>
    public enum GestureBindingKind
    {
        MouseButton = 0,
        KeyboardKey = 1,
    }

    /// <summary>
    /// Modifier keys that may accompany a binding's main input. Flags so multiple modifiers combine.
    /// Matches the layout of System.Windows.Input.ModifierKeys so casts between the two are trivial.
    /// </summary>
    [Flags]
    public enum GestureModifiers
    {
        None = 0,
        Alt = 1,
        Ctrl = 2,
        Shift = 4,
        Win = 8,
    }

    /// <summary>
    /// A user-configurable gesture trigger binding: the key or mouse button (plus optional modifiers)
    /// that must be held to activate gesture drawing. Replaces the legacy single-enum
    /// <see cref="MouseActions"/> "DrawingButton" setting with a more flexible form that accepts any
    /// key, any mouse button, or any Ctrl/Shift/Alt/Win-prefixed combination of either.
    ///
    /// Activation remains "hold-to-draw": press and hold the binding, drag to draw, release to submit.
    /// Modifier matching is exact equality, not subset — a binding of "Ctrl+Space" does NOT activate
    /// when "Ctrl+Shift+Space" is held, so users can safely rely on Shift-prefixed bindings elsewhere.
    /// </summary>
    [Serializable]
    public sealed class GestureBinding
    {
        /// <summary>
        /// Whether the "main" input of this binding is a mouse button or a keyboard key.
        /// </summary>
        public GestureBindingKind Kind { get; set; }

        /// <summary>
        /// The main-input code. For <see cref="GestureBindingKind.MouseButton"/> this is an integer
        /// cast of <see cref="MouseActions"/>. For <see cref="GestureBindingKind.KeyboardKey"/> this
        /// is a Win32 virtual-key code (VK_*).
        /// </summary>
        public int MainCode { get; set; }

        /// <summary>
        /// The modifier keys that must be held simultaneously with the main input for the binding
        /// to activate. Matched exactly (not as a subset).
        /// </summary>
        public GestureModifiers Modifiers { get; set; }

        /// <summary>
        /// True if this binding represents "no binding" — equivalent to the legacy
        /// <see cref="MouseActions.None"/>. Used by the daemon to know whether to install hooks at all.
        /// </summary>
        public bool IsEmpty => Kind == GestureBindingKind.MouseButton && MainCode == (int)MouseActions.None;

        /// <summary>
        /// The canonical "no binding" value. Use this instead of <c>null</c> so call sites don't have
        /// to handle nullability (<see cref="AppConfig"/> is a static class and nullable properties
        /// would ripple through every consumer).
        /// </summary>
        public static GestureBinding None =>
            new GestureBinding { Kind = GestureBindingKind.MouseButton, MainCode = (int)MouseActions.None };

        /// <summary>
        /// Construct a binding from a legacy <see cref="MouseActions"/> value for migration from the
        /// old single-enum "DrawingButton" setting. Modifiers default to <see cref="GestureModifiers.None"/>.
        /// </summary>
        public static GestureBinding FromLegacyMouseAction(MouseActions action)
        {
            return new GestureBinding { Kind = GestureBindingKind.MouseButton, MainCode = (int)action };
        }

        /// <summary>
        /// Serialize to a compact human-readable string for storage in <see cref="AppConfig"/>'s XML
        /// config file. Format: <c>"mouse:Right"</c>, <c>"mouse:Middle+ctrl"</c>, <c>"key:32+ctrl+shift"</c>.
        /// Empty bindings serialize to <c>"mouse:None"</c>.
        /// </summary>
        public string Serialize()
        {
            var sb = new StringBuilder();
            sb.Append(Kind == GestureBindingKind.MouseButton ? "mouse:" : "key:");
            if (Kind == GestureBindingKind.MouseButton)
                sb.Append(((MouseActions)MainCode).ToString());
            else
                sb.Append(MainCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if ((Modifiers & GestureModifiers.Ctrl) != 0) sb.Append("+ctrl");
            if ((Modifiers & GestureModifiers.Shift) != 0) sb.Append("+shift");
            if ((Modifiers & GestureModifiers.Alt) != 0) sb.Append("+alt");
            if ((Modifiers & GestureModifiers.Win) != 0) sb.Append("+win");
            return sb.ToString();
        }

        /// <summary>
        /// Inverse of <see cref="Serialize"/>. Returns <see cref="None"/> on any malformed input
        /// — the parser is forgiving because this runs during app startup and a corrupt config
        /// value must never crash the daemon.
        /// </summary>
        public static GestureBinding Parse(string serialized)
        {
            if (string.IsNullOrWhiteSpace(serialized)) return None;

            var parts = serialized.Split('+');
            var head = parts[0];
            var colonIndex = head.IndexOf(':');
            if (colonIndex < 0) return None;

            var kindStr = head.Substring(0, colonIndex);
            var mainStr = head.Substring(colonIndex + 1);

            var result = new GestureBinding();
            if (kindStr == "mouse")
            {
                result.Kind = GestureBindingKind.MouseButton;
                MouseActions action;
                if (!Enum.TryParse(mainStr, ignoreCase: true, result: out action)) return None;
                result.MainCode = (int)action;
            }
            else if (kindStr == "key")
            {
                result.Kind = GestureBindingKind.KeyboardKey;
                int vk;
                if (!int.TryParse(mainStr, System.Globalization.NumberStyles.Integer,
                                  System.Globalization.CultureInfo.InvariantCulture, out vk)) return None;
                result.MainCode = vk;
            }
            else
            {
                return None;
            }

            for (int i = 1; i < parts.Length; i++)
            {
                switch (parts[i].ToLowerInvariant())
                {
                    case "ctrl":  result.Modifiers |= GestureModifiers.Ctrl; break;
                    case "shift": result.Modifiers |= GestureModifiers.Shift; break;
                    case "alt":   result.Modifiers |= GestureModifiers.Alt; break;
                    case "win":   result.Modifiers |= GestureModifiers.Win; break;
                    default: return None;
                }
            }

            return result;
        }

        /// <summary>
        /// Build a human-readable display string for showing the current binding in the UI.
        /// Mouse buttons are rendered with friendly names (e.g. "Right Button"); keyboard keys
        /// use a <see cref="System.Windows.Forms.Keys"/> cast, which gives names like "Space" / "F13".
        /// Modifiers are prefixed with "+" separators and ordered Ctrl/Shift/Alt/Win.
        /// </summary>
        public string GetDisplayString()
        {
            if (IsEmpty) return "(none)";

            var sb = new StringBuilder();
            if ((Modifiers & GestureModifiers.Ctrl)  != 0) sb.Append("Ctrl+");
            if ((Modifiers & GestureModifiers.Shift) != 0) sb.Append("Shift+");
            if ((Modifiers & GestureModifiers.Alt)   != 0) sb.Append("Alt+");
            if ((Modifiers & GestureModifiers.Win)   != 0) sb.Append("Win+");

            if (Kind == GestureBindingKind.MouseButton)
            {
                switch ((MouseActions)MainCode)
                {
                    case MouseActions.Left:     sb.Append("Left Button"); break;
                    case MouseActions.Right:    sb.Append("Right Button"); break;
                    case MouseActions.Middle:   sb.Append("Middle Button"); break;
                    case MouseActions.XButton1: sb.Append("X Button 1"); break;
                    case MouseActions.XButton2: sb.Append("X Button 2"); break;
                    default:                    sb.Append(((MouseActions)MainCode).ToString()); break;
                }
            }
            else
            {
                // Convert VK code to WPF Key then to its enum name ("Space", "F13", etc.).
                // PresentationCore is already referenced; no new dependency.
                try
                {
                    var key = System.Windows.Input.KeyInterop.KeyFromVirtualKey(MainCode);
                    sb.Append(key == System.Windows.Input.Key.None
                              ? "VK_" + MainCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
                              : key.ToString());
                }
                catch
                {
                    sb.Append("VK_" + MainCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Value equality — two bindings are equal if they have the same kind, main code, and modifiers.
        /// </summary>
        public override bool Equals(object obj)
        {
            var other = obj as GestureBinding;
            if (other == null) return false;
            return Kind == other.Kind && MainCode == other.MainCode && Modifiers == other.Modifiers;
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int h = (int)Kind;
                h = (h * 397) ^ MainCode;
                h = (h * 397) ^ (int)Modifiers;
                return h;
            }
        }

        public override string ToString() => GetDisplayString();
    }
}
