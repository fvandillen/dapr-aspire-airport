namespace Airport.Contracts;

/// <summary>
/// Shared catalog + random-flight generator. Used by FlightOperations' <c>/flights/seed</c>
/// endpoint AND the WASM Operations page so both sides pick from the same realistic set.
/// </summary>
public static class FlightFaker
{
    /// <summary>Airline code prefixes used to build callsigns (e.g. "KL" + 1611).</summary>
    public static readonly string[] AirlineCodes =
    {
        "KL", "BA", "LH", "AF", "U2", "FR", "DL", "UA", "IB", "AZ", "SK", "OS", "LX", "TK", "EK"
    };

    /// <summary>The simulated home airport — all flights depart from here.</summary>
    public const string HomeAirport = "AMS";

    /// <summary>Possible destination IATA codes.</summary>
    public static readonly string[] Destinations =
    {
        "BCN", "LHR", "FRA", "CDG", "MAD", "FCO", "ARN", "VIE", "ZRH", "IST",
        "DUB", "CPH", "OSL", "HEL", "WAW", "ATH", "LIS", "BRU", "MUC", "MXP",
        "JFK", "DXB", "SIN"
    };

    /// <summary>Common short/medium-haul aircraft for the demo.</summary>
    public static readonly string[] AircraftTypes =
    {
        "Boeing 737-800", "Boeing 737 MAX 8", "Boeing 777-300ER", "Boeing 787-9",
        "Airbus A320", "Airbus A320neo", "Airbus A321neo", "Airbus A330-300",
        "Embraer E190", "Embraer E195-E2"
    };

    /// <summary>Concourse letters; combined with a stand number to form a gate code.</summary>
    public static readonly string[] Concourses = { "B", "C", "D", "E", "F", "G" };

    /// <summary>Returns a random schedule request rooted at <paramref name="now"/>.</summary>
    public static ScheduleFlightRequest NewRandom(DateTimeOffset now, Random? rng = null)
    {
        rng ??= Random.Shared;
        var airline = AirlineCodes[rng.Next(AirlineCodes.Length)];
        var flightNumber = rng.Next(100, 9999);
        var callsign = $"{airline}{flightNumber}";

        var destination = Destinations[rng.Next(Destinations.Length)];
        var aircraft = AircraftTypes[rng.Next(AircraftTypes.Length)];
        var gate = $"{Concourses[rng.Next(Concourses.Length)]}{rng.Next(1, 30)}";
        var scheduled = now.AddMinutes(rng.Next(5, 60));

        return new ScheduleFlightRequest(
            Callsign: callsign,
            Origin: HomeAirport,
            Destination: destination,
            AircraftType: aircraft,
            Gate: gate,
            ScheduledDeparture: scheduled);
    }

    /// <summary>Returns <paramref name="count"/> distinct-callsign random requests.</summary>
    public static IReadOnlyList<ScheduleFlightRequest> NewRandomBatch(int count, DateTimeOffset now, Random? rng = null)
    {
        rng ??= Random.Shared;
        var seen = new HashSet<string>();
        var batch = new List<ScheduleFlightRequest>(count);
        // Cap attempts so we never spin forever on a tiny callsign pool.
        for (var i = 0; batch.Count < count && i < count * 10; i++)
        {
            var candidate = NewRandom(now, rng);
            if (seen.Add(candidate.Callsign))
            {
                batch.Add(candidate);
            }
        }
        return batch;
    }
}
