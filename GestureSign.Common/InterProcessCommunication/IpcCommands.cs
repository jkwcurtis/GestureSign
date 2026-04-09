namespace GestureSign.Common.InterProcessCommunication
{
    public enum IpcCommands
    {
        StartControlPanel,
        StartTeaching,
        StopTraining,
        LoadApplications,
        LoadGestures,
        LoadConfiguration,
        GotGesture,
        ConfigReload,
        SynDeviceState,
        Exit,
        // Sent by ControlPanel when the BindingCaptureBox enters/exits capture mode,
        // so the daemon can temporarily suspend its gesture-binding matcher and avoid
        // firing a stray gesture on the very keys/buttons the user is trying to bind.
        PauseBindingCapture,
        ResumeBindingCapture,
    }
}
