using WattScope.Core.Energy;
using WattScope.Core.Monitoring;

namespace WattScope.Core.Tests.TestSupport;

/// <summary>Minimal <see cref="IEnergyStore"/> for monitor logic tests; SQLite behavior is covered in WattScope.Storage.Tests.</summary>
public sealed class InMemoryEnergyStore : IEnergyStore
{
    private readonly Dictionary<long, MonitoringSession> _sessions = [];
    private long _nextId = 1;

    public List<EnergyWriteBatch> Batches { get; } = [];
    public int FailNextWrites { get; set; }

    public EnergyTotals TotalsFor(long sessionId) =>
        Batches.Where(b => b.SessionId == sessionId).SelectMany(b => b.BucketDeltas).Aggregate(new EnergyTotals(), (a, b) => a + b.Totals);

    public IEnumerable<MonitoringGap> Gaps => Batches.SelectMany(b => b.Gaps);

    public void Seed(MonitoringSession session)
    {
        _sessions[session.Id] = session;
        _nextId = Math.Max(_nextId, session.Id + 1);
    }

    public MonitoringSession CreateSession(NewSessionRequest request)
    {
        var session = new MonitoringSession
        {
            Id = _nextId++,
            StartedUtc = request.StartedUtc,
            Status = SessionStatus.Active,
            Estimation = request.Estimation,
            Sensors = request.Sensors
        };
        _sessions[session.Id] = session;
        return session;
    }

    public MonitoringSession? GetSession(long sessionId) => _sessions.GetValueOrDefault(sessionId);

    public MonitoringSession ReopenSession(long sessionId, IReadOnlyList<EnergySensorBinding> sensors)
    {
        var before = _sessions[sessionId];
        _sessions[sessionId] = before with { Status = SessionStatus.Active, EndedUtc = null };
        return before;
    }

    public void WriteBatch(EnergyWriteBatch batch)
    {
        if (FailNextWrites > 0)
        {
            FailNextWrites--;
            throw new IOException("simulated disk failure");
        }
        Batches.Add(batch);
        if (batch.LastObservationUtc is { } last)
            _sessions[batch.SessionId] = _sessions[batch.SessionId] with { LastObservationUtc = last };
    }

    public void EndSession(long sessionId, DateTimeOffset endedUtc, SessionStatus status, string reason) =>
        _sessions[sessionId] = _sessions[sessionId] with { Status = status, EndedUtc = endedUtc, EndReason = reason };
}
