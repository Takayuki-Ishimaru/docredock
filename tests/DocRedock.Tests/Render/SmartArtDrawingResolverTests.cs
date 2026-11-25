using System.Text;
using System.Xml;
using DocRedock.Formats.OpenXml.Drawing;

namespace DocRedock.Tests.Render;

public sealed class SmartArtDrawingResolverTests
{
    private const string D="http://schemas.openxmlformats.org/drawingml/2006/diagram";
    private const string R="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string DS="http://schemas.microsoft.com/office/drawing/2008/diagram";

    [Fact]
    public void Resolves_the_source_owned_cached_drawing_and_retains_shape_tree_and_part_identity()
    {
        var files=Package(); var warnings=new List<string>();
        var result=SmartArtDrawingResolver.TryResolve(RelIds(),"/ppt/slides/slide1.xml", p=>files.GetValueOrDefault(p),warnings);
        Assert.NotNull(result); Assert.Equal("/ppt/diagrams/drawing1.xml",result.PartUri);
        Assert.Equal("spTree",result.ShapeTree.LocalName); Assert.Contains("CACHE_LABEL",result.ShapeTree.InnerText);
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData("TargetMode='External'", "https://example.invalid/drawing.xml")]
    [InlineData("", "../../../../outside.xml")]
    [InlineData("", "../%2e%2e/%2e%2e/%2e%2e/outside.xml")]
    public void External_and_traversing_relationships_are_rejected_without_reading_them(string mode,string target)
    {
        var files=Package();files["/ppt/diagrams/_rels/data1.xml.rels"]=Bytes(Rels("rDraw",target,"http://schemas.microsoft.com/office/2007/relationships/diagramDrawing",mode));
        var reads=new List<string>();var warnings=new List<string>();
        var value=SmartArtDrawingResolver.TryResolve(RelIds(),"/ppt/slides/slide1.xml",p=>{reads.Add(p);return files.GetValueOrDefault(p);},warnings);
        Assert.Null(value);Assert.DoesNotContain(reads,p=>p.Contains("outside",StringComparison.Ordinal)||p.StartsWith("http",StringComparison.Ordinal));Assert.NotEmpty(warnings);
    }

    [Fact]
    public void Does_not_guess_between_ambiguous_cached_drawings_and_refuses_DTDs()
    {
        var files=Package();files["/ppt/diagrams/data1.xml"]=Bytes("<dgm:dataModel xmlns:dgm='"+D+"'/>");
        files["/ppt/diagrams/_rels/data1.xml.rels"]=Bytes("<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='a' Type='http://schemas.microsoft.com/office/2007/relationships/diagramDrawing' Target='drawing1.xml'/><Relationship Id='b' Type='http://schemas.microsoft.com/office/2007/relationships/diagramDrawing' Target='drawing2.xml'/></Relationships>");
        var warnings=new List<string>(); Assert.Null(SmartArtDrawingResolver.TryResolve(RelIds(),"/ppt/slides/slide1.xml",p=>files.GetValueOrDefault(p),warnings));Assert.NotEmpty(warnings);
        files=Package();files["/ppt/diagrams/drawing1.xml"]=Bytes("<!DOCTYPE d [<!ENTITY x 'bad'>]><d>&x;</d>");
        Assert.Null(SmartArtDrawingResolver.TryResolve(RelIds(),"/ppt/slides/slide1.xml",p=>files.GetValueOrDefault(p),warnings));
    }

    [Fact]
    public void Resolution_is_cancellable_and_does_not_change_source_bytes()
    {
        var files=Package();var before=files.ToDictionary(p=>p.Key,p=>p.Value.ToArray());
        using var c=new CancellationTokenSource();c.Cancel();
        Assert.Throws<OperationCanceledException>(()=>SmartArtDrawingResolver.TryResolve(RelIds(),"/ppt/slides/slide1.xml",p=>files.GetValueOrDefault(p),cancellationToken:c.Token));
        _=SmartArtDrawingResolver.TryResolve(RelIds(),"/ppt/slides/slide1.xml",p=>files.GetValueOrDefault(p));
        foreach(var file in files)Assert.Equal(before[file.Key],file.Value);
    }

    [Theory]
    [InlineData("/ppt/../slides/slide1.xml")]
    [InlineData("/ppt/slides/slide1.xml#fragment")]
    [InlineData("/ppt/slides/slide1.xml?query")]
    public void Invalid_owner_URI_is_rejected_before_any_part_read(string owner)
    {
        var reads=0;var warnings=new List<string>();
        Assert.Null(SmartArtDrawingResolver.TryResolve(RelIds(),owner,p=>{reads++;return null;},warnings));
        Assert.Equal(0,reads);Assert.NotEmpty(warnings);
    }

    [Fact]
    public void Duplicate_relationship_ids_and_excessive_depth_are_not_silently_selected()
    {
        var files=Package();var key="/ppt/diagrams/_rels/data1.xml.rels";
        var rel=Rels("rDraw","drawing1.xml","http://schemas.microsoft.com/office/2007/relationships/diagramDrawing");
        files[key]=Bytes(rel.Replace("</Relationships>","<Relationship Id='rDraw' Type='http://schemas.microsoft.com/office/2007/relationships/diagramDrawing' Target='other.xml'/></Relationships>",StringComparison.Ordinal));
        Assert.Null(SmartArtDrawingResolver.TryResolve(RelIds(),"/ppt/slides/slide1.xml",p=>files.GetValueOrDefault(p)));
        files=Package();files["/ppt/diagrams/data1.xml"]=Bytes(string.Concat(Enumerable.Repeat("<n>",150))+string.Concat(Enumerable.Repeat("</n>",150)));
        Assert.Null(SmartArtDrawingResolver.TryResolve(RelIds(),"/ppt/slides/slide1.xml",p=>files.GetValueOrDefault(p)));
    }

    private static XmlElement RelIds(){var d=new XmlDocument { XmlResolver=null };d.LoadXml("<dgm:relIds xmlns:dgm='"+D+"' xmlns:r='"+R+"' r:dm='rData'/>");return d.DocumentElement!;}
    private static byte[] Bytes(string text)=>Encoding.UTF8.GetBytes(text);
    private static string Rels(string id,string target,string type,string extra="")=>"<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='"+id+"' Type='"+type+"' Target='"+target+"' "+extra+"/></Relationships>";
    private static Dictionary<string,byte[]> Package()=>new()
    {
        ["/ppt/slides/_rels/slide1.xml.rels"]=Bytes(Rels("rData","../diagrams/data1.xml",R+"/diagramData")),
        ["/ppt/diagrams/data1.xml"]=Bytes("<dgm:dataModel xmlns:dgm='"+D+"' xmlns:dsp='"+DS+"'><dgm:extLst><dsp:dataModelExt relId='rDraw'/></dgm:extLst></dgm:dataModel>"),
        ["/ppt/diagrams/_rels/data1.xml.rels"]=Bytes(Rels("rDraw","drawing1.xml","http://schemas.microsoft.com/office/2007/relationships/diagramDrawing")),
        ["/ppt/diagrams/drawing1.xml"]=Bytes("<dsp:drawing xmlns:dsp='"+DS+"' xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main'><dsp:spTree><dsp:sp><dsp:nvSpPr><dsp:cNvPr id='3'/></dsp:nvSpPr><dsp:spPr/><dsp:txBody><a:p><a:r><a:t>CACHE_LABEL</a:t></a:r></a:p></dsp:txBody></dsp:sp></dsp:spTree></dsp:drawing>"),
    };
}
