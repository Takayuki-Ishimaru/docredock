namespace DocRedock.Core.Documents;

/// <summary>Normalized text-body layout options from DrawingML a:bodyPr.</summary>
public sealed record DrawingTextLayout
{
    public string Direction { get; init; } = "horz";

    public double RotationDegrees { get; init; }

    public bool Upright { get; init; }

    public bool RightToLeftColumns { get; init; }
}
