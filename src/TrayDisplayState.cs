using System;

namespace IPCountryWatcher
{
    // A retained icon is presentation only: Current always describes the latest query.
    internal sealed class TrayDisplayState
    {
        internal static readonly TimeSpan GracePeriod = TimeSpan.FromMinutes(2);
        private Snapshot confirmed;
        internal Snapshot Current { get; private set; }

        internal void Accept(Snapshot result)
        {
            Current = result;
            if (result.HasCountry) confirmed = result;
            // A newly observed, unresolved IP must never inherit a previous country's flag.
            else if (result.HasIp) confirmed = null;
        }

        internal void Invalidate(bool clearPrevious)
        {
            Current = null;
            if (clearPrevious) confirmed = null;
        }

        internal Snapshot Displayed(DateTime now)
        {
            if (Current != null && Current.HasCountry) return Current;
            if (confirmed == null) return null;
            TimeSpan age = now - confirmed.CheckedUtc;
            return age >= TimeSpan.Zero && age < GracePeriod ? confirmed : null;
        }
    }
}
