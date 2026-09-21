using System;

namespace WinputLan.Core
{
    public static class ReconnectBackoff
    {
        public static TimeSpan DelayForAttempt(int attempt)
        {
            if (attempt < 0) attempt = 0;
            var seconds = Math.Min(15, Math.Pow(2, Math.Min(attempt, 6)) * 0.25);
            return TimeSpan.FromMilliseconds(seconds * 1000);
        }
    }
}
