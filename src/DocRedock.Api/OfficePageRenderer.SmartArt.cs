using System.Globalization;
using System.Text;
using System.IO.Compression;
using System.Xml;
using DocRedock.Formats.OpenXml.Drawing;

namespace DocRedock.Api;

public partial class LibreOfficePageRenderer
{
    /// <summary>Word and Excel SmartArt stays engine-owned, but its visible text must be in the engine's output.</summary>
    private static void RequireSmartArtText(XmlDocument owner,string part,ZipArchive package,List<SmartArtLabel> requiredText,CancellationToken cancellationToken)
    {
        const string a="http://schemas.openxmlformats.org/drawingml/2006/main";
        const string dgm="http://schemas.openxmlformats.org/drawingml/2006/diagram";
        const string relationships="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        byte[]? ReadPart(string uri)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry=package.GetEntry(uri.TrimStart('/'));
            if(entry is null||entry.Length>4*1024*1024)return null;
            return ReadAtMost(entry,4*1024*1024);
        }
        XmlDocument? Load(byte[]? bytes)
        {
            if(bytes is null)return null;
            using var reader=XmlReader.Create(new MemoryStream(bytes),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=4*1024*1024});
            var document=new XmlDocument{XmlResolver=null};document.Load(reader);return document;
        }
        foreach(var ids in owner.GetElementsByTagName("relIds",dgm).OfType<XmlElement>().ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(SmartArtDrawingResolver.TryResolve(ids,"/"+part,ReadPart,cancellationToken:cancellationToken) is {} cached)
            {requiredText.AddRange(ParagraphLabels(cached.ShapeTree.GetElementsByTagName("p",a),null));continue;}
            var slash=part.LastIndexOf('/');
            var rels=Load(ReadPart(part[..(slash+1)]+"_rels/"+part[(slash+1)..]+".rels"));
            var id=ids.GetAttribute("dm",relationships);
            var rel=rels?.DocumentElement?.ChildNodes.OfType<XmlElement>().FirstOrDefault(e=>e.GetAttribute("Id")==id&&e.GetAttribute("TargetMode")!="External");
            if(rel is null||!Uri.TryCreate(new Uri("http://package.invalid/"+part),rel.GetAttribute("Target"),out var uri))continue;
            var data=Load(ReadPart(Uri.UnescapeDataString(uri.AbsolutePath)));
            if(data is not null)requiredText.AddRange(ParagraphLabels(data.GetElementsByTagName("pt",dgm).OfType<XmlElement>().Where(e=>e.GetAttribute("type") is "node" or "").SelectMany(e=>e.GetElementsByTagName("p",a).OfType<XmlElement>()),null));
        }
    }

    /// <summary>One visible SmartArt label (a paragraph); Slide is its 1-based place in the presentation, null for Word and Excel.</summary>
    internal sealed record SmartArtLabel(string Text,int? Slide=null);

    private static IEnumerable<SmartArtLabel> ParagraphLabels(System.Collections.IEnumerable paragraphs,int? slide)
    {
        const string a="http://schemas.openxmlformats.org/drawingml/2006/main";
        foreach(var paragraph in paragraphs.OfType<XmlElement>())
        {
            // A label is a whole paragraph: its runs (and fields) joined, a line break kept as one.
            var text=string.Concat(paragraph.ChildNodes.OfType<XmlElement>().Where(e=>e.NamespaceURI==a).Select(e=>e.LocalName=="br"?"\n":
                e.LocalName is "r" or "fld"?string.Concat(e.GetElementsByTagName("t",a).OfType<XmlElement>().Select(t=>t.InnerText)):""));
            if(text.Length>0)yield return new(text,slide);
        }
    }

    /// <summary>The slide parts of a presentation by their place in it ("ppt/slides/slide7.xml" is the third when the slide list says so).</summary>
    private static Dictionary<string,int> SlideNumbers(ZipArchive package)
    {
        var numbers=new Dictionary<string,int>(StringComparer.Ordinal);
        const string r="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XmlDocument? Load(string name)
        {
            var entry=package.GetEntry(name);
            if(entry is null||entry.Length>4*1024*1024)return null;
            try
            {
                using var reader=XmlReader.Create(new MemoryStream(ReadAtMost(entry,4*1024*1024)??[]),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=4*1024*1024});
                var document=new XmlDocument{XmlResolver=null};document.Load(reader);return document;
            }
            catch(XmlException){return null;}
        }
        var presentation=Load("ppt/presentation.xml");var rels=Load("ppt/_rels/presentation.xml.rels");
        if(presentation is null||rels is null)return numbers;
        var targets=rels.DocumentElement!.ChildNodes.OfType<XmlElement>().Where(e=>e.GetAttribute("TargetMode")!="External")
            .GroupBy(e=>e.GetAttribute("Id"),StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.First().GetAttribute("Target"),StringComparer.Ordinal);
        foreach(var id in presentation.GetElementsByTagName("sldId","http://schemas.openxmlformats.org/presentationml/2006/main").OfType<XmlElement>())
        {
            if(!targets.TryGetValue(id.GetAttribute("id",r),out var target)||!Uri.TryCreate(new Uri("http://package.invalid/ppt/presentation.xml"),target,out var uri))continue;
            numbers.TryAdd(Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/'),numbers.Count+1);
        }
        return numbers;
    }

    private const string WordCharacter=@"[\p{L}\p{N}\p{M}]";
    private const string NonWordCharacters=@"[^\p{L}\p{N}\p{M}]";

    private static string NormalizeLabelText(string value)
    {
        try{value=value.Normalize(NormalizationForm.FormKC);}catch(ArgumentException){}
        return System.Text.RegularExpressions.Regex.Replace(value.ToLowerInvariant(),@"\s+"," ").Trim();
    }

    private static bool IsCjk(Rune rune)=>rune.Value is >=0x2E80 and <=0x9FFF or >=0xAC00 and <=0xD7AF or >=0xF900 and <=0xFAFF or >=0x20000 and <=0x3FFFF;

    /// <summary>The labels the engine's pages do not contain. Both sides are NFKC, lower-case and single-spaced. A Latin or
    /// digit label must appear in the page text as whole words in order, any spacing, line break or punctuation between them
    /// ("A" is not in "Apple"); a CJK label, which has no spaces, as a substring ignoring spacing. The page's paint-order
    /// text, which has no spacing but keeps a rotated label's glyphs together, is a second source for a CJK label and for a
    /// label of four or more letters or digits. A PowerPoint label is looked for on the page of its slide when the engine
    /// made one page per slide, otherwise on any page.</summary>
    internal static IReadOnlyList<string> MissingSmartArtText(IReadOnlyList<SmartArtLabel> labels,IReadOnlyList<(string Text,string? PaintOrder)> pageTexts,int slideCount)
    {
        static string Squeeze(string value)=>string.Concat(value.Where(c=>char.IsLetterOrDigit(c)||char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark));
        var pages=pageTexts.Select(page=>NormalizeLabelText(page.Text)).ToArray();
        var squeezed=pages.Select(Squeeze).ToArray();
        var painted=pageTexts.Select(page=>Squeeze(NormalizeLabelText(page.PaintOrder??""))).ToArray();
        var missing=new List<string>();
        foreach(var label in labels.DistinctBy(l=>(l.Text,l.Slide)))
        {
            var text=NormalizeLabelText(label.Text);
            if(!text.Any(char.IsLetterOrDigit))continue;
            var compact=Squeeze(text);
            var cjk=text.EnumerateRunes().Any(IsCjk);
            var words=System.Text.RegularExpressions.Regex.Matches(text,WordCharacter+"+").Select(m=>System.Text.RegularExpressions.Regex.Escape(m.Value));
            var pattern="(?<!"+WordCharacter+")"+string.Join(NonWordCharacters+"+",words)+"(?!"+WordCharacter+")";
            var candidates=label.Slide is {} slide&&slideCount>0&&pages.Length==slideCount&&slide>=1&&slide<=slideCount?new[]{slide-1}:Enumerable.Range(0,pages.Length);
            bool Present(int page)
            {
                if(cjk)return squeezed[page].Contains(compact,StringComparison.Ordinal)||painted[page].Contains(compact,StringComparison.Ordinal);
                try{if(System.Text.RegularExpressions.Regex.IsMatch(pages[page],pattern,System.Text.RegularExpressions.RegexOptions.CultureInvariant,TimeSpan.FromSeconds(2)))return true;}
                catch(System.Text.RegularExpressions.RegexMatchTimeoutException){return squeezed[page].Contains(compact,StringComparison.Ordinal);}
                return compact.Length>=4&&painted[page].Contains(compact,StringComparison.Ordinal);
            }
            if(!candidates.Any(Present))missing.Add(label.Text.Trim());
        }
        return missing;
    }

    private static void ExpandSavedSmartArt(XmlDocument slide,string part,ZipArchive package,List<SmartArtLabel> requiredText,int? slideNumber,CancellationToken cancellationToken)
    {
        const string p="http://schemas.openxmlformats.org/presentationml/2006/main";
        const string a="http://schemas.openxmlformats.org/drawingml/2006/main";
        const string dsp="http://schemas.microsoft.com/office/drawing/2008/diagram";
        const string dgm="http://schemas.openxmlformats.org/drawingml/2006/diagram";
        var highest=slide.GetElementsByTagName("cNvPr",p).OfType<XmlElement>().Select(e=>int.TryParse(e.GetAttribute("id"),out var id)?id:0).DefaultIfEmpty().Max();
        byte[]? ReadPart(string uri)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry=package.GetEntry(uri.TrimStart('/'));
            if(entry is null||entry.Length>4*1024*1024)return null;
            return ReadAtMost(entry,4*1024*1024);
        }
        foreach(var ids in slide.GetElementsByTagName("relIds",dgm).OfType<XmlElement>().ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame=ids.ParentNode?.ParentNode?.ParentNode as XmlElement;
            if(frame?.LocalName!="graphicFrame")continue;
            var cached=SmartArtDrawingResolver.TryResolve(ids,"/"+part,ReadPart,cancellationToken:cancellationToken);
            if(cached is null)
            {
                // Uncached layout remains engine-owned, but a successful export must actually contain its visible text.
                var relations=package.GetEntry(part[..part.LastIndexOf('/')] + "/_rels/"+Path.GetFileName(part)+".rels");
                if(relations is not null)
                {
                    using var stream=relations.Open();var rels=new XmlDocument{XmlResolver=null};
                    using var reader=XmlReader.Create(stream,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=4*1024*1024});rels.Load(reader);
                    var id=ids.GetAttribute("dm","http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                    var rel=rels.DocumentElement!.ChildNodes.OfType<XmlElement>().FirstOrDefault(e=>e.GetAttribute("Id")==id&&e.GetAttribute("TargetMode")!="External");
                    if(rel is not null)
                    {
                        var uri=new Uri(new Uri("http://package.invalid/"+part),rel.GetAttribute("Target"));
                        var bytes=ReadPart(uri.AbsolutePath);
                        if(bytes is not null)
                        {
                            using var sourceReader=XmlReader.Create(new MemoryStream(bytes),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=4*1024*1024});
                            var data=new XmlDocument{XmlResolver=null};data.Load(sourceReader);
                            requiredText.AddRange(ParagraphLabels(data.GetElementsByTagName("pt",dgm).OfType<XmlElement>().Where(e=>e.GetAttribute("type") is "node" or "").SelectMany(e=>e.GetElementsByTagName("p",a).OfType<XmlElement>()),slideNumber));
                        }
                    }
                }
                continue;
            }
            requiredText.AddRange(ParagraphLabels(cached.ShapeTree.GetElementsByTagName("p",a),slideNumber));
            if(cached.ShapeTree.SelectNodes(".//@*[namespace-uri()='http://schemas.openxmlformats.org/officeDocument/2006/relationships']")!.Count>0)
                throw new NotSupportedException("Saved SmartArt media needs its native source application; structured source geometry remains available.");
            XmlElement Convert(XmlElement source)
            {
                var element=slide.CreateElement(source.NamespaceURI==dsp?"p":source.Prefix,source.LocalName,source.NamespaceURI==dsp?p:source.NamespaceURI);
                foreach(XmlAttribute attr in source.Attributes)
                    if(attr.Prefix!="xmlns"&&attr.Name!="xmlns"&&attr.LocalName!="modelId")element.Attributes.Append((XmlAttribute)slide.ImportNode(attr,true));
                foreach(var child in source.ChildNodes.OfType<XmlElement>())element.AppendChild(Convert(child));
                if(element.LocalName is "nvSpPr" or "nvGrpSpPr" or "nvCxnSpPr" && element.NamespaceURI==p&&
                    !element.ChildNodes.OfType<XmlElement>().Any(e=>e.LocalName=="nvPr"))element.AppendChild(slide.CreateElement("p","nvPr",p));
                if(source.ChildNodes.OfType<XmlText>().Any())element.InnerText=source.InnerText;
                if(element.NamespaceURI==p&&element.LocalName=="sp"&&
                    element.SelectSingleNode("./*[local-name()='spPr']/*[local-name()='prstGeom']") is XmlElement geometry&&
                    geometry.GetAttribute("prst")=="straightConnector1"&&element.SelectSingleNode("./*[local-name()='txBody']") is null)
                {
                    // DSP stores a straight connector as a shape; Impress requires a real PML
                    // connector for a zero-height line and its two arrowheads to remain visible.
                    var connector=slide.CreateElement("p","cxnSp",p);
                    foreach(var child in element.ChildNodes.OfType<XmlElement>().ToArray())
                    {
                        if(child.LocalName=="nvSpPr")
                        {
                            var nonVisual=slide.CreateElement("p","nvCxnSpPr",p);
                            foreach(var item in child.ChildNodes.OfType<XmlElement>().ToArray())
                            {
                                if(item.LocalName=="cNvSpPr")
                                {
                                    var props=slide.CreateElement("p","cNvCxnSpPr",p);
                                    foreach(XmlAttribute attr in item.Attributes)props.Attributes.Append((XmlAttribute)slide.ImportNode(attr,true));
                                    foreach(var nested in item.ChildNodes.OfType<XmlElement>())props.AppendChild(nested.CloneNode(true));
                                    nonVisual.AppendChild(props);
                                }
                                else nonVisual.AppendChild(item);
                            }
                            connector.AppendChild(nonVisual);
                        }
                        else connector.AppendChild(child);
                    }
                    return connector;
                }
                return element;
            }
            var owner=slide.CreateElement("p","grpSp",p);
            var nv=slide.CreateElement("p","nvGrpSpPr",p);owner.AppendChild(nv);
            var identity=frame.SelectSingleNode("./*[local-name()='nvGraphicFramePr']/*[local-name()='cNvPr']") as XmlElement;
            if(identity is null)continue;
            nv.AppendChild(identity.CloneNode(true));nv.AppendChild(slide.CreateElement("p","cNvGrpSpPr",p));nv.AppendChild(slide.CreateElement("p","nvPr",p));
            var properties=slide.CreateElement("p","grpSpPr",p);owner.AppendChild(properties);
            var original=frame.SelectSingleNode("./*[local-name()='xfrm']") as XmlElement;
            var natural=cached.ShapeTree.SelectSingleNode("./*[local-name()='grpSpPr']/*[local-name()='xfrm']/*[local-name()='ext']") as XmlElement;
            if(original is null||natural is null||!double.TryParse(natural.GetAttribute("cx"),NumberStyles.Float,CultureInfo.InvariantCulture,out var width)||
                !double.TryParse(natural.GetAttribute("cy"),NumberStyles.Float,CultureInfo.InvariantCulture,out var height)||!double.IsFinite(width)||!double.IsFinite(height)||width<=0||height<=0)continue;
            var transform=slide.CreateElement("a","xfrm",a);properties.AppendChild(transform);
            foreach(XmlAttribute attr in original.Attributes)transform.SetAttribute(attr.Name,attr.Value);
            foreach(var child in original.ChildNodes.OfType<XmlElement>())transform.AppendChild(child.CloneNode(true));
            var offset=slide.CreateElement("a","chOff",a);offset.SetAttribute("x","0");offset.SetAttribute("y","0");transform.AppendChild(offset);
            var extent=slide.CreateElement("a","chExt",a);extent.SetAttribute("cx",natural.GetAttribute("cx"));extent.SetAttribute("cy",natural.GetAttribute("cy"));transform.AppendChild(extent);
            var inner=slide.CreateElement("p","grpSp",p);
            foreach(var child in cached.ShapeTree.ChildNodes.OfType<XmlElement>())inner.AppendChild(Convert(child));
            var mapping=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var ident in inner.GetElementsByTagName("cNvPr",p).OfType<XmlElement>())
            {var value=(++highest).ToString(CultureInfo.InvariantCulture);mapping[ident.GetAttribute("id")]=value;ident.SetAttribute("id",value);}
            foreach(var connection in inner.SelectNodes(".//*[local-name()='stCxn' or local-name()='endCxn']")!.OfType<XmlElement>())
                if(mapping.TryGetValue(connection.GetAttribute("id"),out var value))connection.SetAttribute("id",value);
            owner.AppendChild(inner);frame.ParentNode!.ReplaceChild(owner,frame);
        }
    }
}
