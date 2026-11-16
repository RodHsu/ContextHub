using Memory.Infrastructure;

var clock = new LinuxRequestArrivalClock();
if (!clock.TryReadDomain(out var domain) || domain is null ||
    domain.MonotonicOffsetNs != 0 || domain.BoottimeOffsetNs != 0)
    return 2;
RequestArrivalClockReading? previous = null;
for (var index = 0; index < 64; index++)
{
    if (!clock.TryRead(out var reading) || reading is null ||
        reading.RawAfterNs < reading.RawBeforeNs || reading.RawAfterNs - reading.RawBeforeNs > 50_000_000 ||
        previous is not null && reading.RawBeforeNs < previous.RawAfterNs)
        return 3;
    previous = reading;
    Thread.Sleep(1);
}
if (!clock.TryReadDomain(out var after) || after != domain) return 4;
Console.WriteLine("ARRIVAL_NATIVE_CLOCK_READ_PASS samples=64 domainStable=true settingsChanged=false");
return 0;
