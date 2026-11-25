using DocRedock.Core.Documents;

namespace DocRedock.Render;

/// <summary>Sparse source dimensions. Stored OOXML column widths already contain their padding.</summary>
internal sealed class WorkbookSourceAxis
{
    private readonly int[] _keys;
    private readonly double[] _deltas;
    private readonly double _default;
    public int Maximum { get; }
    public WorkbookSourceAxis(WorkbookSheetPresentation? sheet, bool columns, bool includeHidden)
    {
        Maximum = columns ? 16384 : 1048576;
        _default = columns ? ColumnPixels(sheet?.DefaultColumnWidth ?? 8.43) : (sheet?.DefaultRowHeight ?? 15) * 96 / 72;
        var sizes = (columns ? sheet?.ColumnWidths : sheet?.RowHeights) ?? new Dictionary<int,double>();
        var hidden = includeHidden ? new HashSet<int>() : ((columns ? sheet?.HiddenColumns : sheet?.HiddenRows) ?? []).ToHashSet();
        _keys = sizes.Keys.Concat(hidden).Where(k => k > 0 && k <= Maximum).Distinct().Order().ToArray();
        _deltas = new double[_keys.Length + 1];
        for (var i=0; i<_keys.Length; i++)
        {
            var value = sizes.TryGetValue(_keys[i],out var size) ? columns ? ColumnPixels(size) : size*96/72 : _default;
            _deltas[i+1] = _deltas[i] + (hidden.Contains(_keys[i]) ? 0 : value) - _default;
        }
    }
    public static double ColumnPixels(double width) => Math.Truncate(((256*width+Math.Truncate(128d/7))/256)*7);
    public double Start(int index)
    {
        index = Math.Clamp(index,1,Maximum+1);
        var n=Array.BinarySearch(_keys,index); if(n<0)n=~n;
        return (index-1)*_default+_deltas[n];
    }
    public double Size(int index) => Start(index+1)-Start(index);
    public int Index(double position)
    {
        var lo=1;var hi=Maximum;
        while(lo<hi){var mid=lo+(hi-lo+1)/2;if(Start(mid)<=position)lo=mid;else hi=mid-1;}
        return lo;
    }
    public double Map(double coordinate, WorkbookSourceAxis source)
    {
        var index=source.Index(coordinate); var size=source.Size(index);
        return Start(index)+(size>0?(coordinate-source.Start(index))*Size(index)/size:0);
    }
}
