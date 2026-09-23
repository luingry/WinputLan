namespace WinputLan.Core
{
    // Keeps close/tray-exit decisions deterministic and independently testable.
    public sealed class BackgroundLifecycle
    {
        private bool _explicitExit;
        private bool _cleanupStarted;
        public BackgroundLifecycle(bool continueInBackground) { ContinueInBackground = continueInBackground; }
        public bool ContinueInBackground { get; set; }
        public bool ShouldHideOnClose { get { return ContinueInBackground && !_explicitExit; } }
        public void RequestExplicitExit() { _explicitExit = true; }
        public bool TryBeginCleanup() { if (_cleanupStarted) return false; _cleanupStarted = true; return true; }
    }
}
