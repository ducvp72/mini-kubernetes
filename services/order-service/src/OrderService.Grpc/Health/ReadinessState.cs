namespace OrderService.Grpc.Health;

/// <summary>
/// Thread-safe readiness latch flipped to draining on SIGTERM (ApplicationStopping)
/// so /readyz returns 503 while /healthz continues returning 200 during in-flight drain.
/// </summary>
public sealed class ReadinessState
{
    private const int Starting = 0;
    private const int Ready = 1;
    private const int Draining = 2;

    private int _state = Starting;

    public bool IsReady => Volatile.Read(ref _state) == Ready;

    public string StatusLabel => Volatile.Read(ref _state) switch
    {
        Ready => "ready",
        Draining => "draining",
        _ => "starting"
    };

    public void MarkReady() => Interlocked.CompareExchange(ref _state, Ready, Starting);

    public void MarkDraining() => Interlocked.Exchange(ref _state, Draining);
}
