namespace WinputLan.Core
{
    // Mouse movement remains local so absolute coordinates continue to advance.
    // Discrete input is suppressed only after it has been accepted for remote delivery.
    public static class InputCapturePolicy
    {
        public static bool ShouldSuppressPublished(InputKind kind, bool published)
        {
            return published && kind != InputKind.MouseMove;
        }
    }
}
