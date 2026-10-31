using System;

namespace IPCountryWatcher
{
    // A retained icon is presentation only: Current always describes the latest query.
    internal sealed class TrayDisplayState
    {
        internal static readonly TimeSpan GracePeriod = TimeSpan.FromMinutes(2);
        private Snapshot confirmed;
        private int unconfirmedRounds;
        internal Snapshot Current { get; private set; }
        // Set by the tray's fast link loop; Unknown and Healthy never change the badge.
        internal LinkState Link { get; set; }
        // One fully failed HTTPS round is a suspicion; the next consecutive one confirms it.
        internal bool FailureConfirmed { get { return unconfirmedRounds >= 2; } }

        internal void Accept(Snapshot result)
        {
            Current = result;
            unconfirmedRounds = result.Connectivity != null && result.Connectivity.Health == ConnectivityHealth.Unreachable ?
                unconfirmedRounds + 1 : 0;
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
            if (Current != null && Current.HasCountry && IsRecent(Current.CheckedUtc, now, GracePeriod)) return Current;
            if (confirmed == null) return null;
            TimeSpan age = now - confirmed.CheckedUtc;
            return age >= TimeSpan.Zero && age < GracePeriod ? confirmed : null;
        }

        internal bool IsIpFresh(DateTime now)
        {
            return Current != null && Current.HasIp && IsRecent(Current.CheckedUtc, now, ConnectivitySnapshot.MaximumAge);
        }

        internal ConnectivityHealth Health(DateTime now)
        {
            return Current != null && Current.Connectivity != null && Current.Connectivity.IsFresh(now) &&
                IsRecent(Current.CheckedUtc, now, ConnectivitySnapshot.MaximumAge) ? Current.Connectivity.Health : ConnectivityHealth.Checking;
        }

        internal ConnectionBadge Badge(DateTime now)
        {
            // The fast loop has already confirmed the outage over several rounds.
            if (Link == LinkState.Down) return ConnectionBadge.Unconfirmed;
            ConnectivityHealth health = Health(now);
            if (health == ConnectivityHealth.Unreachable) return FailureConfirmed ? ConnectionBadge.Unconfirmed : ConnectionBadge.Limited;
            if (health == ConnectivityHealth.Limited) return ConnectionBadge.Limited;
            if (health == ConnectivityHealth.Checking || !IsIpFresh(now)) return ConnectionBadge.Pending;
            if (Link == LinkState.Suspect || Link == LinkState.Degraded || Link == LinkState.Recovering) return ConnectionBadge.Limited;
            if (Current != null && Current.CountryIsStale) return ConnectionBadge.Limited;
            return ConnectionBadge.None;
        }

        private static bool IsRecent(DateTime observed, DateTime now, TimeSpan lifetime)
        { return now >= observed && now - observed < lifetime; }
    }
}
