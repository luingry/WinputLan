using System;

namespace WinputLan.Core
{
    public static class RequestAttemptOwnership
    {
        public static bool IsCurrent(object currentAttempt, object candidateAttempt)
        {
            return ReferenceEquals(currentAttempt, candidateAttempt);
        }
    }
}
