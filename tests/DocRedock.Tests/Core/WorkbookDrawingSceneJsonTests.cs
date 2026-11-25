using System.Text.Json;
using DocRedock.Core.Documents;

namespace DocRedock.Tests.Core;

public sealed class WorkbookDrawingSceneJsonTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"sheet-A\":null}")]
    [InlineData("{\"sheet-A\":{\"sheetName\":\"A\",\"objects\":[null]}}")]
    [InlineData("{\"sheet-A\":{\"sheetName\":\"A\",\"objects\":null}}")]
    public void Malformed_scene_is_bounded_and_does_not_throw(string json)
    {
        using var raw=JsonDocument.Parse(json);
        var result=WorkbookDrawingSceneJson.Read(raw.RootElement);
        Assert.All(result.Values,scene=>Assert.Empty(scene.Objects));
    }
    [Fact]
    public void Source_geometry_and_text_round_trip_without_markup_execution()
    {
        var item=new WorkbookDrawingObject("shape","shape",120,50,new(E:80,F:26)){Text="<script> **$A$**",HeadArrow="triangle",TailArrow="triangle"};
        var scene=new WorkbookDrawingScene("A",[item]);
        var restored=WorkbookDrawingSceneJson.Read(WorkbookDrawingSceneJson.Write(new Dictionary<string,WorkbookDrawingScene>{{"sheet-A",scene}}));
        var actual=Assert.Single(restored["sheet-A"].Objects);
        Assert.Equal(item.Text,actual.Text);Assert.Equal(item.Transform,actual.Transform);Assert.Equal(item.HeadArrow,actual.HeadArrow);Assert.Equal(item.TailArrow,actual.TailArrow);
    }
    [Theory]
    [InlineData("text", "null")]
    [InlineData("stroke", "null")]
    [InlineData("fontFamily", "null")]
    [InlineData("paths", "[null]")]
    [InlineData("transform", "null")]
    public void Null_properties_are_rejected_before_rendering(string key,string value)
    {
        var baseline=WorkbookDrawingSceneJson.Write(new Dictionary<string,WorkbookDrawingScene>{{"sheet-A",new("A",[new("shape","shape",10,20,new())])}});
        var node=System.Text.Json.Nodes.JsonNode.Parse(baseline.GetRawText())!;
        node["sheet-A"]!["objects"]![0]![key]=System.Text.Json.Nodes.JsonNode.Parse(value);
        var result=WorkbookDrawingSceneJson.Read(JsonSerializer.SerializeToElement(node));
        Assert.Empty(result["sheet-A"].Objects);
    }
    [Fact]
    public void An_oversized_value_is_reported_once_instead_of_silently_dropped()
    {
        var warnings=new List<string>();
        using var raw=JsonDocument.Parse("{\"sheet-A\":{\"sheetName\":\""+new string('x',33*1024*1024)+"\",\"objects\":[]}}");
        Assert.Empty(WorkbookDrawingSceneJson.Read(raw.RootElement,warnings));
        Assert.Equal("Invalid or oversized workbook drawing scenes; none of their objects are drawn.",Assert.Single(warnings));
    }
    [Fact]
    public void Invalid_objects_are_counted_in_one_warning_per_sheet_and_the_valid_ones_stay()
    {
        var good=new WorkbookDrawingObject("ok","shape",10,20,new());
        var bad=new WorkbookDrawingObject("bad","shape",200_000_000,20,new());
        var warnings=new List<string>();
        var result=WorkbookDrawingSceneJson.Read(WorkbookDrawingSceneJson.Write(new Dictionary<string,WorkbookDrawingScene>{{"sheet-A",new("A",[good,bad,bad])}}),warnings);
        Assert.Equal("ok",Assert.Single(result["sheet-A"].Objects).Id);
        Assert.Equal("Invalid workbook drawing objects in sheet-A; 2 of 3 are omitted.",Assert.Single(warnings));
    }
    [Fact]
    public void A_sheet_over_the_object_limit_is_reported_once_and_other_sheets_stay()
    {
        var many=Enumerable.Range(0,WorkbookDrawingSceneJson.MaxObjects+1).Select(i=>new WorkbookDrawingObject("o"+i,"shape",1,1,new())).ToArray();
        var warnings=new List<string>();
        var result=WorkbookDrawingSceneJson.Read(WorkbookDrawingSceneJson.Write(new Dictionary<string,WorkbookDrawingScene>
            {{"sheet-A",new("A",many)},{"sheet-B",new("B",[new("ok","shape",1,1,new())])}}),warnings);
        Assert.False(result.ContainsKey("sheet-A"));Assert.Single(result["sheet-B"].Objects);
        Assert.Equal("Invalid workbook drawing scene or object count above 16384 in sheet-A; its objects are omitted.",Assert.Single(warnings));
    }
    [Fact]
    public void Scenes_beyond_the_sheet_limit_are_reported_once_and_the_first_ones_stay()
    {
        var scenes=Enumerable.Range(0,4097).ToDictionary(i=>"sheet-"+i,i=>new WorkbookDrawingScene("S"+i,[]));
        var warnings=new List<string>();
        var result=WorkbookDrawingSceneJson.Read(WorkbookDrawingSceneJson.Write(scenes),warnings);
        Assert.Equal(4096,result.Count);
        Assert.Equal("Workbook drawing scene limit exceeded; the scenes of the remaining sheets are omitted.",Assert.Single(warnings));
    }
    [Fact]
    public void Invalid_json_shape_is_reported()
    {
        var warnings=new List<string>();
        using var raw=JsonDocument.Parse("{\"sheet-A\":{\"sheetName\":\"A\",\"objects\":\"x\"}}");
        Assert.Empty(WorkbookDrawingSceneJson.Read(raw.RootElement,warnings));
        Assert.StartsWith("Invalid workbook drawing scene JSON: ",Assert.Single(warnings),StringComparison.Ordinal);
    }
    [Fact]
    public void The_readable_projection_surfaces_the_drawing_scene_warning_once()
    {
        var bad=new WorkbookDrawingObject("bad","shape",200_000_000,20,new());
        var graph=new DocumentGraph(DocumentGraph.CurrentSchemaVersion,"doc_t",DocumentFormatKind.Xlsx,
            [new DocumentPartition("sheet-A",0,[])],
            FormatExtensions:new Dictionary<string,JsonElement>{[WorkbookDrawingSceneJson.ExtensionKey]=
                WorkbookDrawingSceneJson.Write(new Dictionary<string,WorkbookDrawingScene>{{"sheet-A",new("A",[bad])}})});
        var result=DocRedock.Markdown.ReadableProjectionBuilder.Build(graph);
        var diagnostic=Assert.Single(result.Diagnostics,d=>d.Code=="WorkbookDrawingSceneInvalid");
        Assert.Contains("sheet-A",diagnostic.Message,StringComparison.Ordinal);
    }
}
