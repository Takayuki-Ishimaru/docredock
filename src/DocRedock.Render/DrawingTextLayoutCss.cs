using DocRedock.Core.Documents;

namespace DocRedock.Render;

/// <summary>Projects normalized text-layout options onto native CSS writing-mode/text-orientation keywords.</summary>
public static class DrawingTextLayoutCss
{
    public static string WritingMode(DrawingTextLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return layout.Direction switch {
        "horz" => "horizontal-tb",
        "vert" or "eaVert" or "wordArtVertRtl" => "vertical-rl",
        "vert270" => "sideways-lr",
        "wordArtVert" or "mongolianVert" => "vertical-lr",
        _ => "horizontal-tb",
        };
    }

    public static string TextOrientation(DrawingTextLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return layout.Direction switch {
        "vert" => "sideways",
        "wordArtVert" or "wordArtVertRtl" => "upright",
        _ => "mixed",
        };
    }
}
