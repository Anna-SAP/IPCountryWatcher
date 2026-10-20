namespace IPCountryWatcher
{
    internal sealed class EgressChange
    {
        internal readonly Snapshot Previous;
        internal readonly Snapshot Current;
        internal EgressChange(Snapshot previous, Snapshot current) { Previous = previous; Current = current; }
        internal bool CountryChanged { get { return Previous.CountryCode != Current.CountryCode; } }
    }

    // Changes compare confirmed results only: an IP without a country waits for its lookup, so a new IP
    // and its flag are reported together. Failures keep the baseline across outages.
    internal sealed class EgressChangeTracker
    {
        private Snapshot confirmed;

        // Returns the change from the previously confirmed result when the IP or country differs, otherwise null.
        internal EgressChange Observe(Snapshot result)
        {
            if (!result.HasCountry) return null;
            Snapshot previous = confirmed;
            confirmed = result;
            return previous != null && (previous.CountryCode != result.CountryCode || previous.Ip != result.Ip)
                ? new EgressChange(previous, result) : null;
        }
    }
}
