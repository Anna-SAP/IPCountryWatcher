namespace IPCountryWatcher
{
    // Change notifications compare confirmed countries only: a new IP in the same country stays silent,
    // and an IP without a country waits for its lookup. Failures keep the baseline across outages.
    internal sealed class CountryChangeTracker
    {
        private Snapshot confirmed;

        // Returns the previously confirmed result when the country differs, otherwise null.
        internal Snapshot Observe(Snapshot result)
        {
            if (!result.HasCountry) return null;
            Snapshot previous = confirmed;
            confirmed = result;
            return previous != null && previous.CountryCode != result.CountryCode ? previous : null;
        }
    }
}
