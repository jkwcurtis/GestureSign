# GestureSign (jkwcurtis fork)

> **This is a fork of [TransposonY/GestureSign](https://github.com/TransposonY/GestureSign)** with one substantial feature added on top of upstream v8.1: **custom press-to-capture gesture trigger bindings**. If you want the unmodified upstream app, go there. If you want to draw gestures by holding a keyboard key, a modifier + mouse button, or any combination thereof, keep reading.

GestureSign is a gesture recognition app for Windows. You draw a shape with your mouse (or finger on a touch device), and GestureSign matches it against a library of shapes to fire an action — launch an app, send keystrokes, switch windows, adjust volume, and so on.

## What this fork adds

Upstream GestureSign lets you pick *one of four* mouse buttons as the gesture trigger: **Right, Middle, X1, or X2**. That's it. You can't use Left (it would break every click), you can't use a keyboard key, and you can't use a modifier chord.

This fork replaces that dropdown with a **Logitech Options-style press-to-capture binding editor**. You click `Change`, press whatever you want, and the app records it. Supported trigger types:

- **Any single mouse button** — Right, Middle, Left (at your own risk), X1, X2
- **Any single keyboard key** — `Space`, `F13`, `Caps Lock`, any letter, arrow keys, etc.
- **Modifier + mouse button** — `Ctrl+MiddleClick`, `Shift+X1`, `Alt+Right`, etc.
- **Modifier + keyboard key** — `Ctrl+Space`, `Ctrl+Shift+G`, `Win+F13`, etc.

The activation model is unchanged: **hold the binding, drag the mouse to draw, release to submit.** No toggles, no double-taps, no chord sequences. If you've been using right-click gestures, the workflow is identical — you're just picking a different trigger.

### Under the hood

- Modifier matching is **exact**, not subset: `Ctrl+Space` will NOT fire on `Ctrl+Shift+Space`. This means you can safely have Shift-prefixed bindings for other apps without them getting eaten.
- **Modifier keys always pass through.** Holding Ctrl for a `Ctrl+MiddleClick` binding doesn't prevent Ctrl from working in other apps — only the "main" key/button of the binding is consumed.
- **Mouse-binding fall-through is preserved.** If you bind `Right` and tap without drawing, you still get the context menu — GestureSign simulates the click on release, same as upstream.
- **Keyboard-binding tap-without-draw is swallowed.** A keyboard binding is a deliberate dedicated trigger — tapping it without drawing eats the keystroke rather than leaking it to the foreground app. If you bound `Space`, tapping it won't type a space.
- **Existing configs migrate invisibly.** If you had `DrawingButton = Right` from upstream GestureSign, the first launch of this fork reads it as the new binding format automatically. No config surgery needed.

For the full design rationale and implementation notes, see [`docs/superpowers/specs/2026-04-08-custom-gesture-binding-design.md`](docs/superpowers/specs/2026-04-08-custom-gesture-binding-design.md).

## Build environment note

This fork is built without Visual Studio — just the .NET Framework 4.6.1 reference assemblies pulled from NuGet. As a side effect:

- **The "Launch Windows Store App" action is disabled** in this build. That plugin depends on a Windows 10 SDK `Windows.winmd` that's only available with VS Build Tools installed. All other actions work normally.
- **`GestureSign.ExtraPlugins`** (TextCopyer and ClipboardMatch) are excluded from the solution build for the same reason.

If you install **Visual Studio Build Tools 2022 with the "Managed Desktop Build Tools" workload**, you can revert the build-env commit (`964df3f`) and rebuild to restore those plugins. The feature commits don't depend on any of this.

[![Release](https://img.shields.io/github/release/TransposonY/GestureSign.svg?style=flat-square)](https://github.com/TransposonY/GestureSign/releases/latest)

## Feature

- Activate Window
- Window Control
- Touch Keyboard Control
- Keyboard simulation
- Key Down/Up
- Mouse Simulation
- Send Keystrokes
- Open Default Browser
- Screen Brightness
- Volume Adjustment
- Run Command or Program
- Send Message
- Toggle Window Topmost
