namespace WindowProbe.Remote;

/// <summary>Uma entrega por intervalo fixo, sem acumular atraso de agendamento.</summary>
internal sealed class VideoFramePacer(int fps)
{
    private long lastSlot = -1;

    public bool TryAccept(TimeSpan elapsed)
    {
        long slot = elapsed.Ticks * fps / TimeSpan.TicksPerSecond;
        if (slot <= lastSlot) return false;
        lastSlot = slot;
        return true;
    }
}
