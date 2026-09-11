using System;
using System.Runtime.InteropServices;

namespace Novawake.Helpers
{
    public static class SleepPreventer
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);

        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;

        private static bool isPreventing = false;
        private static uint _lastFlags = ES_CONTINUOUS;

        public static bool IsPreventing => isPreventing;

        public static bool Start(bool preventDisplaySleep = true)
        {
            uint flags = ES_CONTINUOUS | ES_SYSTEM_REQUIRED;
            if (preventDisplaySleep)
            {
                flags |= ES_DISPLAY_REQUIRED;
            }

            uint result = SetThreadExecutionState(flags);
            if (result == 0)
            {
                isPreventing = false;
                _lastFlags = ES_CONTINUOUS;
                return false;
            }

            _lastFlags = flags;
            isPreventing = true;
            return true;
        }

        /// <summary>
        /// Re-applies the last execution state flags. Call this periodically
        /// (e.g., on every timer tick) because SetThreadExecutionState is
        /// thread-bound and the lock can expire if the calling thread changes.
        /// </summary>
        public static bool Refresh()
        {
            if (!isPreventing)
            {
                return false;
            }

            if (SetThreadExecutionState(_lastFlags) == 0)
            {
                isPreventing = false;
                _lastFlags = ES_CONTINUOUS;
                return false;
            }

            return true;
        }

        public static bool Stop()
        {
            uint result = SetThreadExecutionState(ES_CONTINUOUS);
            // Only mark as stopped if the API call succeeded
            if (result != 0)
            {
                isPreventing = false;
                _lastFlags = ES_CONTINUOUS;
                return true;
            }

            return false;
        }
    }
}
