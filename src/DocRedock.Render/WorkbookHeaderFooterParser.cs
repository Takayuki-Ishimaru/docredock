using System.Globalization;
using System.Text;

namespace DocRedock.Render;

internal sealed record WorkbookFooterRun(string Text, char Field='\0', int PageOffset=0,
    string Font="Calibri", double Size=11, string Color="#000000", bool Bold=false,
    bool Italic=false, bool Underline=false, bool Strike=false, bool Superscript=false, bool Subscript=false,
    bool UnresolvedThemeColor=false);
internal sealed record WorkbookFooterSection(string Alignment, IReadOnlyList<WorkbookFooterRun> Runs);

/// <summary>Excel header/footer control codes. Produces typed data, never markup.</summary>
internal static class WorkbookHeaderFooterParser
{
    public static IReadOnlyList<WorkbookFooterSection> Parse(string? source, IReadOnlyList<string?>? themeColors = null)
    {
        if (string.IsNullOrEmpty(source)) return [];
        var sections=new Dictionary<string,List<WorkbookFooterRun>>(StringComparer.Ordinal)
        { ["left"]=[],["center"]=[],["right"]=[] };
        var alignment="center";var style=new WorkbookFooterRun("");var text=new StringBuilder();
        void Flush(){if(text.Length>0){sections[alignment].Add(style with{Text=text.ToString()});text.Clear();}}
        for(var i=0;i<source.Length;i++)
        {
            var c=source[i];
            if(c!='&'||i+1==source.Length){text.Append(c);continue;}
            var code=source[++i];
            if(code=='&'){text.Append('&');continue;}
            Flush();
            if(code is 'L' or 'C' or 'R'){alignment=code=='L'?"left":code=='R'?"right":"center";continue;}
            if(code=='"')
            {
                var end=source.IndexOf('"',i+1);
                if(end<0){text.Append("&\"");continue;}
                var font=source[(i+1)..end].Split(',',2);var face=font.Length==2?font[1]:"Regular";
                style=style with{Font=font[0].Length>0?font[0]:style.Font,Bold=face.Contains("Bold",StringComparison.OrdinalIgnoreCase),Italic=face.Contains("Italic",StringComparison.OrdinalIgnoreCase)};
                i=end;continue;
            }
            if(char.IsAsciiDigit(code))
            {
                var start=i;while(i+1<source.Length&&(char.IsAsciiDigit(source[i+1])||source[i+1]=='.'))i++;
                if(double.TryParse(source[start..(i+1)],NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var size)&&size is >0 and <=4096)style=style with{Size=size};
                continue;
            }
            if(code=='K'&&i+5<source.Length && char.IsAsciiDigit(source[i+1]) && char.IsAsciiDigit(source[i+2]) &&
                source[i+3] is '+' or '-' && char.IsAsciiDigit(source[i+4]) && char.IsAsciiDigit(source[i+5]))
            {
                var slot=(source[i+1]-'0')*10+source[i+2]-'0';
                var amount=((source[i+4]-'0')*10+source[i+5]-'0')/100d*(source[i+3]=='-'?-1:1);
                if(themeColors is not null && slot<themeColors.Count && themeColors[slot] is {Length:7} rgb &&
                    rgb[0]=='#' && rgb[1..].All(char.IsAsciiHexDigit)) style=style with{Color=Tint(rgb,amount),UnresolvedThemeColor=false};
                else style=style with{UnresolvedThemeColor=true};
                i+=5;continue;
            }
            if(code=='K'&&i+6<source.Length)
            {
                var color=source.Substring(i+1,6);
                if(color.All(char.IsAsciiHexDigit)){style=style with{Color="#"+color,UnresolvedThemeColor=false};i+=6;continue;}
            }
            if(code is 'P' or 'N' or 'A' or 'F' or 'Z' or 'D' or 'T' or 'G')
            {
                var offset=0;
                if(code is 'P' or 'N'&&i+2<source.Length&&source[i+1] is '+' or '-')
                {
                    var start=i+1;var end=start+1;while(end<source.Length&&char.IsAsciiDigit(source[end]))end++;
                    if(end>start+1&&int.TryParse(source[start..end],NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out var value)){offset=Math.Clamp(value,-32767,32767);i=end-1;}
                }
                sections[alignment].Add(style with{Field=code,PageOffset=offset});continue;
            }
            style=code switch
            {
                'B'=>style with{Bold=!style.Bold},'I'=>style with{Italic=!style.Italic},
                'U' or 'E'=>style with{Underline=!style.Underline},'S'=>style with{Strike=!style.Strike},
                'X' when !style.Subscript=>style with{Superscript=!style.Superscript},'Y' when !style.Superscript=>style with{Subscript=!style.Subscript},
                _=>style,
            };
            if(code is not ('B' or 'I' or 'U' or 'E' or 'S' or 'X' or 'Y' or 'O' or 'H'))text.Append('&').Append(code);
        }
        Flush();return sections.Where(s=>s.Value.Count>0).Select(s=>new WorkbookFooterSection(s.Key,s.Value)).ToArray();
    }

    private static string Tint(string hex, double tint)
    {
        var rgb=new[]{Convert.ToInt32(hex[1..3],16)/255d,Convert.ToInt32(hex[3..5],16)/255d,Convert.ToInt32(hex[5..7],16)/255d};
        var max=rgb.Max();var min=rgb.Min();var delta=max-min;var light=(max+min)/2;
        var sat=delta==0?0:delta/(1-Math.Abs(2*light-1));
        var hue=delta==0?0:max==rgb[0]?((rgb[1]-rgb[2])/delta+(rgb[1]<rgb[2]?6:0))/6:
            max==rgb[1]?((rgb[2]-rgb[0])/delta+2)/6:((rgb[0]-rgb[1])/delta+4)/6;
        light=tint<0?light*(1+tint):light*(1-tint)+tint;
        var q=light<.5?light*(1+sat):light+sat-light*sat;var p=2*light-q;
        int Channel(double t){t=(t+1)%1;var value=sat==0?light:t<1d/6?p+(q-p)*6*t:t<.5?q:t<2d/3?p+(q-p)*(2d/3-t)*6:p;return Math.Clamp((int)Math.Round(value*255),0,255);}
        return $"#{Channel(hue+1d/3):X2}{Channel(hue):X2}{Channel(hue-1d/3):X2}";
    }
}
