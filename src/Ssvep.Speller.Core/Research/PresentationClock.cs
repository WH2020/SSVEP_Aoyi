namespace Ssvep.Speller.Core.Research;

public sealed class PresentationClock
{
    private double? origin;
    public double? RenderingSeconds { get; private set; }
    public double PhaseSeconds { get; private set; }
    public long Epoch { get; private set; }
    public void Reset() { origin=null; RenderingSeconds=null; PhaseSeconds=0; Epoch++; }
    public bool Advance(double renderingSeconds)
    {
        if(!double.IsFinite(renderingSeconds)) throw new ArgumentException("无效呈现时刻。");
        if(RenderingSeconds==renderingSeconds) return false;
        if(RenderingSeconds is double previous&&renderingSeconds<previous) Reset();
        origin??=renderingSeconds; RenderingSeconds=renderingSeconds; PhaseSeconds=renderingSeconds-origin.Value;
        return true;
    }
}
