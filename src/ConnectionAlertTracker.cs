using System;

namespace IPCountryWatcher
{
    internal enum ConnectionAlert { None, Failed, Unconfirmed, Restored }

    // Reports each outage once, then its end once. Every refresh, network switch or login-time Wi-Fi
    // connection briefly leaves the IP unconfirmed, so only a failed check after a confirmed connection
    // alerts at once; a pending check, a partial result without an IP or a failure before the first
    // confirmation alerts only when it persists. The red and amber badges flapping during backoff stay silent.
    internal sealed class ConnectionAlertTracker
    {
        internal static readonly TimeSpan PersistFor = TimeSpan.FromSeconds(15);
        private bool confirmedBefore;
        private bool alerted;
        private DateTime? unconfirmedSinceUtc;

        internal ConnectionAlert Observe(DateTime now, bool ipFresh, ConnectivityHealth health)
        {
            if (ipFresh)
            {
                bool restored = alerted;
                confirmedBefore = true;
                alerted = false;
                unconfirmedSinceUtc = null;
                return restored ? ConnectionAlert.Restored : ConnectionAlert.None;
            }
            // A clock moved backwards restarts the wait instead of producing a negative or huge duration.
            if (unconfirmedSinceUtc == null || now < unconfirmedSinceUtc.Value) unconfirmedSinceUtc = now;
            if (alerted) return ConnectionAlert.None;
            bool failed = health == ConnectivityHealth.Unreachable;
            if (!(failed && confirmedBefore) && now - unconfirmedSinceUtc.Value < PersistFor) return ConnectionAlert.None;
            alerted = true;
            return failed ? ConnectionAlert.Failed : ConnectionAlert.Unconfirmed;
        }
    }
}
