using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DocRedock.Core.Documents;
using DocRedock.Formats.OpenXml.Docx;
using DocRedock.Markdown;

namespace DocRedock.Tests.Docx;

public sealed class DocxR0RemediationTests
{
 [Fact] public async Task R0_FIX_01_cross_paragraph_labels_and_exact_adjacent_edges()
 {
  var e=await E(Node("n1",0,"DOC_FLOW_START")+Node("n2",1000,"DOC_FLOW_CHECK")+Node("n3",2000,"DOC_FLOW_DONE")+Line("l1",420)+Line("l2",1420)); var v=V(e); var m=M(e);
  Assert.Equal(3,v.Nodes.Count); Assert.Equal(2,v.Edges.Count);
  Assert.Contains(v.Edges,z=>L(v,z.SourceId)=="DOC_FLOW_START"&&L(v,z.TargetId)=="DOC_FLOW_CHECK");
  Assert.Contains(v.Edges,z=>L(v,z.SourceId)=="DOC_FLOW_CHECK"&&L(v,z.TargetId)=="DOC_FLOW_DONE");
  Assert.DoesNotContain(v.Edges,z=>L(v,z.SourceId)=="DOC_FLOW_START"&&L(v,z.TargetId)=="DOC_FLOW_DONE");
  Assert.Equal(1,Count(m,"DOC_FLOW_START")); Assert.Equal(1,Count(m,"DOC_FLOW_CHECK")); Assert.Equal(1,Count(m,"DOC_FLOW_DONE"));
 }
 [Fact] public async Task R0_FIX_02_literal_vml_line_without_arrow_is_undirected()
 {
  var v=V(await E(Node("n1",0,"DOC_FLOW_START")+Node("n2",1000,"DOC_FLOW_CHECK")+VmlLine("vl1","420,90","980,90")));
  var z=Assert.Single(v.Edges); Assert.Equal(VisualEdgeDirection.Undirected,z.EdgeDirection); Assert.Equal("DOC_FLOW_START",L(v,z.SourceId)); Assert.Equal("DOC_FLOW_CHECK",L(v,z.TargetId));
 }
 [Fact] public async Task R0_FIX_02_literal_vml_line_endarrow_is_forward()
 {
  var v=V(await E(Node("n1",0,"DOC_FLOW_START")+Node("n2",1000,"DOC_FLOW_CHECK")+VmlLine("vl1","420,90","980,90",true)));
  var z=Assert.Single(v.Edges); Assert.Equal(VisualEdgeDirection.Directed,z.EdgeDirection); Assert.Equal("DOC_FLOW_START",L(v,z.SourceId)); Assert.Equal("DOC_FLOW_CHECK",L(v,z.TargetId));
 }
 [Fact] public async Task R0_FIX_02_literal_vml_line_reversed_endarrow_is_reverse()
 {
  var v=V(await E(Node("n1",0,"DOC_FLOW_START")+Node("n2",1000,"DOC_FLOW_CHECK")+VmlLine("vl1","980,90","420,90",true)));
  var z=Assert.Single(v.Edges); Assert.Equal(VisualEdgeDirection.Directed,z.EdgeDirection); Assert.Equal("DOC_FLOW_CHECK",L(v,z.SourceId)); Assert.Equal("DOC_FLOW_START",L(v,z.TargetId));
 }
 [Fact] public async Task R1_P0_3_direct_vml_rect_and_oval_nodes_project_labels_and_relation()
 {
  var v=V(await E(VmlShape("n1","VML_START",0,"rect")+VmlShape("n2","VML_CHECK",1000,"oval")+VmlLine("vl1","420,90","980,90",true)));
  Assert.Contains(v.Nodes,n=>n.Label=="VML_START"); Assert.Contains(v.Nodes,n=>n.Label=="VML_CHECK");
  Assert.Contains(v.Edges,z=>z.SourceId is not null&&z.TargetId is not null);
 }
 [Fact] public async Task R0_FIX_03_flip_with_arrowhead_resolves_left_direction()
 {
  var x=Node("n1",0,"DOC_FLOW_START")+Node("n2",1000,"DOC_FLOW_CHECK")+Line("l1",420,90,true); var v=V(await E(x)); var z=Assert.Single(v.Edges);
  Assert.Equal(VisualEdgeDirection.Directed,z.EdgeDirection); Assert.Equal("DOC_FLOW_CHECK",L(v,z.SourceId)); Assert.Equal("DOC_FLOW_START",L(v,z.TargetId));
 }
 [Fact] public async Task R0_FIX_03_flip_without_arrowhead_is_undirected()
 {
  var x=Node("n1",0,"DOC_FLOW_START")+Node("n2",1000,"DOC_FLOW_CHECK")+Line("l1",420,90,false,true); var v=V(await E(x)); var z=Assert.Single(v.Edges);
  Assert.Equal(VisualEdgeDirection.Undirected,z.EdgeDirection);
 }
 [Fact] public async Task R0_FIX_04_equidistant_targets_are_unresolved_and_labels_remain()
 {
  var e=await E(Node("n1",0,"DOC_FLOW_START")+Node("b1",1000,"TARGET_A",-300)+Node("b2",1000,"TARGET_B",300)+Line("l1",420,100)); var v=V(e); var m=M(e);
  Assert.DoesNotContain(v.Edges,z=>z.SourceId is not null&&z.TargetId is not null); Assert.Contains(v.Diagnostics!,d=>d.Code=="VisualConnectorUnresolved"&&d.SourceObjectId=="l1"); Assert.Contains("TARGET_A",m); Assert.Contains("TARGET_B",m);
 }
 [Fact] public async Task R3_S0_1_connector_crossing_an_intermediate_node_stays_unresolved()
 {
  var e=await E(Node("n1",0,"SKIP_A")+Node("n2",1000,"SKIP_B")+Node("n3",2000,"SKIP_C")+LineWithExtent("skip",420,90,1560)); var v=V(e); var m=M(e);
  Assert.DoesNotContain(v.Edges,z=>z.SourceId is not null&&z.TargetId is not null);
  Assert.Contains(v.Diagnostics!,d=>d.Code=="VisualConnectorUnresolved"&&d.SourceObjectId=="skip");
  Assert.Contains("SKIP_A",m); Assert.Contains("SKIP_B",m); Assert.Contains("SKIP_C",m);
  Assert.DoesNotContain("SKIP_A --- SKIP_C",m,StringComparison.Ordinal); Assert.DoesNotContain("SKIP_A --> SKIP_C",m,StringComparison.Ordinal); Assert.DoesNotContain("SKIP_A → SKIP_C",m,StringComparison.Ordinal);
 }
 [Fact] public async Task R3_S0_2_ambiguous_native_alias_is_not_relocked_by_the_adapter()
 {
  var e=await E(Node("dup",0,"DUP_A")+Node("dup",1000,"DUP_B")+Node("target",2000,"TARGET")+NativeConnector("native","dup","target",420,90,1560)); var v=V(e);
  Assert.DoesNotContain(v.Edges,z=>z.SourceId is not null&&z.TargetId is not null);
  Assert.Contains(v.Diagnostics!,d=>d.Code=="VisualNativeAliasAmbiguous"&&d.SourceObjectId=="native");
  Assert.Contains(v.Diagnostics!,d=>d.Code=="VisualConnectorUnresolved"&&d.SourceObjectId=="native");
 }
 [Fact] public async Task R0_FIX_05_anchor_coordinates_preserve_three_node_topology()
 {
  var e=await E(AnchorNode("n1",0,0,"DOC_FLOW_START")+AnchorNode("n2",1000,0,"DOC_FLOW_CHECK")+AnchorNode("n3",2000,0,"DOC_FLOW_DONE")+AnchorLine("l1",420,90)+AnchorLine("l2",1420,90)); var v=V(e);
  Assert.Equal(3,v.Nodes.Count); Assert.Equal(2,v.Edges.Count); Assert.DoesNotContain(v.Edges,z=>z.SourceId is not null&&z.TargetId is not null&&L(v,z.SourceId)=="DOC_FLOW_START"&&L(v,z.TargetId)=="DOC_FLOW_DONE");
 }
 [Fact] public async Task R0_FIX_05_character_relative_anchor_is_not_merged()
 {
  var e=await E(AnchorNode("n1",0,0,"DOC_FLOW_START")+AnchorNode("n2",1000,0,"DOC_FLOW_CHECK","character")+AnchorLine("l1",420,90)); var v=V(e);
  Assert.DoesNotContain(v.Edges,z=>z.SourceId is not null&&z.TargetId is not null); Assert.Contains(v.Diagnostics!,d=>d.Code=="VisualCoordinateSpaceIncompatible");
 }
 [Fact] public async Task R4_two_independent_unsnapped_diagrams_are_clustered_without_cross_edges()
 {
  var body=Node("a",0,"DOC_R4_A")+Node("b",1000,"DOC_R4_B")+Node("c",2400,"DOC_R4_C")+Node("d",3400,"DOC_R4_D")+BatchLine("ab",420,90)+BatchLine("cd",2820,90);
  var path=await D(body); var adapter=new DocxAdapter(); var first=await adapter.ExtractAsync(path); var second=await adapter.ExtractAsync(path);
  var graphs=Vs(first); Assert.Equal(2,graphs.Length);
  var edges=graphs.SelectMany(graph=>graph.Edges).ToArray(); Assert.Equal(2,edges.Length);
  Assert.Contains(edges,edge=>L(graphs.Single(graph=>graph.Nodes.Any(node=>node.Id==edge.SourceId)),edge.SourceId)=="DOC_R4_A"&&L(graphs.Single(graph=>graph.Nodes.Any(node=>node.Id==edge.TargetId)),edge.TargetId)=="DOC_R4_B");
  Assert.Contains(edges,edge=>L(graphs.Single(graph=>graph.Nodes.Any(node=>node.Id==edge.SourceId)),edge.SourceId)=="DOC_R4_C"&&L(graphs.Single(graph=>graph.Nodes.Any(node=>node.Id==edge.TargetId)),edge.TargetId)=="DOC_R4_D");
  Assert.DoesNotContain(edges,edge=>edge.SourceId is not null&&edge.TargetId is not null&&((L(graphs.Single(graph=>graph.Nodes.Any(node=>node.Id==edge.SourceId)),edge.SourceId).StartsWith("DOC_R4_A",StringComparison.Ordinal)&&L(graphs.Single(graph=>graph.Nodes.Any(node=>node.Id==edge.TargetId)),edge.TargetId).StartsWith("DOC_R4_C",StringComparison.Ordinal))||(L(graphs.Single(graph=>graph.Nodes.Any(node=>node.Id==edge.SourceId)),edge.SourceId).StartsWith("DOC_R4_C",StringComparison.Ordinal)&&L(graphs.Single(graph=>graph.Nodes.Any(node=>node.Id==edge.TargetId)),edge.TargetId).StartsWith("DOC_R4_A",StringComparison.Ordinal))));
  Assert.All(edges,edge=>Assert.False(string.IsNullOrWhiteSpace(edge.Evidence?.ClusterId))); Assert.Equal(2,edges.Select(edge=>edge.Evidence!.ClusterId).Distinct(StringComparer.Ordinal).Count());
  Assert.DoesNotContain(edges,edge=>edge.SourceId is not null&&edge.TargetId is not null&&((L(graphs.Single(graph=>graph.Nodes.Any(node=>node.Id==edge.SourceId)),edge.SourceId).StartsWith("DOC_R4_A",StringComparison.Ordinal)&&L(graphs.Single(graph=>graph.Nodes.Any(node=>node.Id==edge.TargetId)),edge.TargetId).StartsWith("DOC_R4_C",StringComparison.Ordinal))||(L(graphs.Single(graph=>graph.Nodes.Any(node=>node.Id==edge.SourceId)),edge.SourceId).StartsWith("DOC_R4_C",StringComparison.Ordinal)&&L(graphs.Single(graph=>graph.Nodes.Any(node=>node.Id==edge.TargetId)),edge.TargetId).StartsWith("DOC_R4_A",StringComparison.Ordinal))));
  Assert.Equal(JsonSerializer.Serialize(graphs),JsonSerializer.Serialize(Vs(second)));
 }
 static string M(DocxExtractionResult e)=>new ReadableMarkdownSerializer().Serialize(e.Graph);
 static VisualGraph V(DocxExtractionResult e){var n=Assert.Single(e.Graph.Nodes,n=>n.Kind==NodeKind.Diagram&&n.Extensions?.ContainsKey("visual_graph")==true);return n.Extensions!["visual_graph"].Deserialize<VisualGraph>()!;}
 static VisualGraph[] Vs(DocxExtractionResult e)=>e.Graph.Nodes.Where(n=>n.Kind==NodeKind.Diagram&&n.Extensions?.ContainsKey("visual_graph")==true).Select(n=>n.Extensions!["visual_graph"].Deserialize<VisualGraph>()!).ToArray();
 static string L(VisualGraph v,string? id)=>v.Nodes.Single(n=>n.Id==id).Label;
 static int Count(string s,string x){var n=0;for(var i=0;(i=s.IndexOf(x,i,StringComparison.Ordinal))>=0;i+=x.Length)n++;return n;}
 [Fact] public async Task R4_two_nearby_diagrams_with_fifty_connectors_keep_cluster_limits_local()
 {
  var firstNodes=string.Concat(Enumerable.Range(0,26).Select(i=>Node($"na{i}",i*1000,$"DOC_R4_NEAR_A{i}")));
  var secondNodes=string.Concat(Enumerable.Range(0,26).Select(i=>Node($"nc{i}",i*1000,$"DOC_R4_NEAR_C{i}",1000)));
  var firstLines=string.Concat(Enumerable.Range(0,25).Select(i=>BatchLine($"ab{i}",i*1000+420,90)));
  var secondLines=string.Concat(Enumerable.Range(0,25).Select(i=>BatchLine($"cd{i}",i*1000+420,1090)));
  var body=firstNodes+secondNodes+firstLines+secondLines;
  var e=await E(body); var graphs=Vs(e); var edges=graphs.SelectMany(graph=>graph.Edges).ToArray(); var diagnostics=graphs.SelectMany(graph=>graph.Diagnostics??[]).ToArray();
  Assert.Equal(50,graphs.Sum(graph=>graph.Edges.Count)); Assert.True(graphs.Length==2, string.Join(";",graphs.Select(graph=>graph.Nodes.Count+":"+string.Join(",",graph.Edges.Select(edge=>edge.Resolution+":"+edge.SourceId+":"+edge.TargetId+":"+edge.Evidence?.ClusterId)))));
  Assert.All(graphs,graph=>{Assert.True(graph.HasTopology);Assert.True(graph.SourceAccounting.IsConsistent);});
  Assert.Equal(2,graphs.SelectMany(graph=>graph.Edges).Select(edge=>edge.Evidence!.ClusterId).Distinct(StringComparer.Ordinal).Count());
  var firstNodeIds=graphs.Single(graph=>graph.Nodes.Any(node=>node.Label.StartsWith("DOC_R4_NEAR_A",StringComparison.Ordinal))).Nodes.Select(node=>node.Id).ToHashSet(StringComparer.Ordinal);
  Assert.DoesNotContain(edges,edge=>firstNodeIds.Contains(edge.SourceId??string.Empty)!=firstNodeIds.Contains(edge.TargetId??string.Empty));
  Assert.DoesNotContain(edges,edge=>edge.SourceId is not null&&edge.TargetId is not null&&((L(graphs.Single(graph=>graph.Nodes.Any(node=>node.Id==edge.SourceId)),edge.SourceId).StartsWith("DOC_R4_NEAR_A",StringComparison.Ordinal)&&L(graphs.Single(graph=>graph.Nodes.Any(node=>node.Id==edge.TargetId)),edge.TargetId).StartsWith("DOC_R4_NEAR_C",StringComparison.Ordinal))||(L(graphs.Single(graph=>graph.Nodes.Any(node=>node.Id==edge.SourceId)),edge.SourceId).StartsWith("DOC_R4_NEAR_C",StringComparison.Ordinal)&&L(graphs.Single(graph=>graph.Nodes.Any(node=>node.Id==edge.TargetId)),edge.TargetId).StartsWith("DOC_R4_NEAR_A",StringComparison.Ordinal))));
  Assert.DoesNotContain(diagnostics,diagnostic=>diagnostic.Code=="VisualClusterLimitExceeded");

 }
 [Fact] public async Task R3_S0_4_wps_shape_absent_from_engine_candidates_becomes_edge_label_not_isolated_node()
 {
  // Contract round-3 regression: a wps:wsp shape (YES) sits ~0.55x the node minor-axis median
  // away from the only connector's segment, but ~190 units from either endpoint -- far enough
  // that it never enters SoftConnectionEngine's Candidates for either end (see DocxAdapter's
  // orphan-shape label demotion pass). It is the sole candidate for the sole connector, so
  // EdgeLabelAssigner accepts it unconditionally: this lands on outcome (a), `-->|YES|`, once,
  // per the remediation brief -- never a `[YES]` isolated Mermaid node.
  var e=await E(Node("n1",0,"DOC_FLOW_START")+Node("n2",1000,"DOC_FLOW_CHECK")+Node("label",600,"YES",150,200,100)+BatchLine("l1",420,90)); var v=V(e); var m=M(e);
  Assert.Equal(2,v.Nodes.Count); Assert.DoesNotContain(v.Nodes,n=>n.Label=="YES");
  var edge=Assert.Single(v.Edges); Assert.Equal("YES",edge.Label);
  Assert.Equal("DOC_FLOW_START",L(v,edge.SourceId)); Assert.Equal("DOC_FLOW_CHECK",L(v,edge.TargetId));
  Assert.Equal(1,Count(m,"YES")); Assert.Contains(" -->|YES| ",m,StringComparison.Ordinal); Assert.DoesNotContain("[YES]",m,StringComparison.Ordinal);
 }
 [Fact] public async Task R3_S0_5_docx_no_diagrams_option_lists_relations_once_without_mermaid()
 {
  // F3-2 (DOCX half): with ReadableMarkdownOptions.IncludeDiagrams=false (the --no-diagrams CLI
  // flag), the visual graph falls back to one readable relation list instead of Mermaid, and
  // node text must not be duplicated. Reuses R0_FIX_01's cross-paragraph 3-node/2-edge fixture;
  // dynamically confirmed to yield exactly one relation list and one DOC_FLOW_START occurrence.
  var e=await E(Node("n1",0,"DOC_FLOW_START")+Node("n2",1000,"DOC_FLOW_CHECK")+Node("n3",2000,"DOC_FLOW_DONE")+Line("l1",420)+Line("l2",1420));
  var markdown=new ReadableMarkdownSerializer(new ReadableMarkdownOptions(IncludeDiagrams:false)).Serialize(e.Graph);
  Assert.DoesNotContain("```mermaid",markdown,StringComparison.Ordinal);
  Assert.Equal(1,Count(markdown,"### 図の接続関係")); Assert.Equal(1,Count(markdown,"DOC_FLOW_START"));
 }
 static string Node(string id,int x,string t,int y=0,int w=400,int h=200)=>$"<w:p><w:r><w:drawing><wps:wsp><a:cNvPr id=\"{id}\"/><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{w}\" cy=\"{h}\"/></a:xfrm><w:txbxContent><w:p><w:r><w:t>{t}</w:t></w:r></w:p></w:txbxContent></wps:wsp></w:drawing></w:r></w:p>";
 static string Line(string id,int x,int y=90,bool arrow=false,bool noArrowFlip=false)=>$"<w:p><w:r><w:drawing><wps:wsp><a:cNvPr id=\"{id}\"/><a:prstGeom prst=\"line\"/><a:xfrm{(noArrowFlip||arrow?" flipH=\"1\"":"")}><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"560\" cy=\"0\"/></a:xfrm>{(arrow?"<a:ln><a:tailEnd type=\"triangle\"/></a:ln>":"")}</wps:wsp></w:drawing></w:r></w:p>";
 static string LineWithExtent(string id,int x,int y,int extent)=>$"<w:p><w:r><w:drawing><wps:wsp><a:cNvPr id=\"{id}\"/><a:prstGeom prst=\"line\"/><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{extent}\" cy=\"0\"/></a:xfrm></wps:wsp></w:drawing></w:r></w:p>";
 static string NativeConnector(string id,string start,string end,int x,int y,int extent)=>$"<w:p><w:r><w:drawing><a:cxnSp><a:nvCxnSpPr><a:cNvPr id=\"{id}\"/><a:cNvCxnSpPr><a:stCxn id=\"{start}\"/><a:endCxn id=\"{end}\"/></a:cNvCxnSpPr></a:nvCxnSpPr><a:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{extent}\" cy=\"0\"/></a:xfrm><a:prstGeom prst=\"line\"/></a:spPr></a:cxnSp></w:drawing></w:r></w:p>";
 static string BatchLine(string id,int x,int y)=>$"<w:p><w:r><w:drawing><wps:wsp><a:cNvPr id=\"{id}\"/><a:prstGeom prst=\"line\"/><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"560\" cy=\"0\"/></a:xfrm><a:ln><a:tailEnd type=\"triangle\"/></a:ln></wps:wsp></w:drawing></w:r></w:p>";
 static string VmlLine(string id,string from,string to,bool arrow=false)
 {
  var stroke=arrow ? "><v:stroke endarrow='block'/></v:line>" : "/>";
  return $"<w:p><w:r><w:pict><v:line id=\"{id}\" from=\"{from}\" to=\"{to}\"{stroke}</w:pict></w:r></w:p>";
 } static string VmlShape(string id,string t,int x,string kind)=>$"<w:p><w:r><w:pict><v:{kind} id=\"{id}\" style=\"margin-left:{x}pt;margin-top:0pt;width:400pt;height:200pt\"><v:textbox><w:txbxContent><w:p><w:r><w:t>{t}</w:t></w:r></w:p></w:txbxContent></v:textbox></v:{kind}></w:pict></w:r></w:p>";
 static string AnchorNode(string id,int x,int y,string t,string relative="page")=>$"<w:p><w:r><w:drawing><wp:anchor><wp:positionH relativeFrom=\"{relative}\"><wp:posOffset>{x}</wp:posOffset></wp:positionH><wp:positionV relativeFrom=\"{relative}\"><wp:posOffset>{y}</wp:posOffset></wp:positionV><wp:extent cx=\"400\" cy=\"200\"/><a:graphic><a:graphicData><wps:wsp><a:cNvPr id=\"{id}\"/><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"400\" cy=\"200\"/></a:xfrm><w:txbxContent><w:p><w:r><w:t>{t}</w:t></w:r></w:p></w:txbxContent></wps:wsp></a:graphicData></a:graphic></wp:anchor></w:drawing></w:r></w:p>";
 static string AnchorLine(string id,int x,int y)=>AnchorNode(id,x,y,"","page")
  .Replace("<wp:extent cx=\"400\" cy=\"200\"/>","<wp:extent cx=\"560\" cy=\"0\"/>",StringComparison.Ordinal)
  .Replace("<a:ext cx=\"400\" cy=\"200\"/>","<a:ext cx=\"560\" cy=\"0\"/>",StringComparison.Ordinal)
  .Replace("<w:txbxContent><w:p><w:r><w:t></w:t></w:r></w:p></w:txbxContent>","<a:prstGeom prst=\"line\"/>",StringComparison.Ordinal);
 static async Task<DocxExtractionResult> E(string b)=>await new DocxAdapter().ExtractAsync(await D(b));
 static async Task<string> D(string b){var d=Path.Combine(Path.GetTempPath(),"docredock-r0",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(d);var p=Path.Combine(d,"f.docx");await using var f=File.Create(p);using var z=new ZipArchive(f,ZipArchiveMode.Create);await W(z,"[Content_Types].xml","<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");await W(z,"_rels/.rels","<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"/>");await W(z,"word/document.xml",$"<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" xmlns:v=\"urn:schemas-microsoft-com:vml\"><w:body>{b}</w:body></w:document>");return p;}
 static async Task W(ZipArchive z,string n,string s){await using var w=new StreamWriter(z.CreateEntry(n).Open(),new UTF8Encoding(false));await w.WriteAsync(s);}
}
