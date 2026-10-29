using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;

namespace DyOne.Spec.Extraction;

/// <summary>
/// PPTX·DOCX 추출. python-pptx / python-docx 가 텍스트를 만드는 방식을 XML 수준에서 그대로 따른다.
/// (그래야 dyone-pilot 분석 결과와 같은 조항이 나온다)
/// </summary>
internal static class OfficeExtract
{
    private const string NsA = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string NsP = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string NsW = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string TableUri = "http://schemas.openxmlformats.org/drawingml/2006/table";
    private static readonly HashSet<string> ShapeTags = ["sp", "grpSp", "graphicFrame", "cxnSp", "pic", "contentPart"];
    private static readonly Regex PageCounter = new(@"^\d+\s*/\s*\d+\s*p?$", RegexOptions.Compiled);

    // ================================================================ PPTX
    public static (int, List<Block>) Pptx(byte[] data)
    {
        using var stream = new MemoryStream(data);
        using var doc = PresentationDocument.Open(stream, false);
        var pres = doc.PresentationPart!;
        var slideIds = pres.Presentation.SlideIdList?.ChildElements.Where(e => e.LocalName == "sldId").ToList() ?? [];
        var blocks = new List<Block>();
        var slideNo = 0;
        foreach (var sid in slideIds)
        {
            slideNo++;
            var rid = sid.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships").Value;
            if (string.IsNullOrEmpty(rid) || pres.GetPartById(rid) is not SlidePart slidePart) continue;
            var tree = Child(Child(slidePart.Slide, "cSld", NsP), "spTree", NsP);
            if (tree is null) continue;
            var layout = slidePart.SlideLayoutPart;
            var master = layout?.SlideMasterPart;
            var shapes = tree.ChildElements.Where(e => e.NamespaceUri == NsP && ShapeTags.Contains(e.LocalName))
                .Select((e, i) => (e, i, top: Top(e, layout, master) ?? 0, left: Left(e, layout, master) ?? 0))
                .OrderBy(s => s.top).ThenBy(s => s.left).ThenBy(s => s.i) // 안정 정렬(python sorted)
                .Select(s => s.e).ToList();
            var title = "";
            var rowNo = 0;
            var tableNo = 0;
            var texts = new List<Block>();
            foreach (var shape in shapes)
            {
                if (shape.LocalName == "graphicFrame" && TableOf(shape) is { } tbl)
                {
                    tableNo++;
                    foreach (var tr in tbl.ChildElements.Where(e => e.LocalName == "tr" && e.NamespaceUri == NsA))
                    {
                        var cells = tr.ChildElements.Where(e => e.LocalName == "tc" && e.NamespaceUri == NsA)
                            .Select(tc => IsSpanned(tc) ? "" : SpecText.Clean(TextBody(Child(tc, "txBody", NsA)))).ToList();
                        if (cells.Any(c => c.Length > 0))
                        {
                            rowNo++;
                            texts.Add(new Block("slide", slideNo, "", rowNo, tableNo, cells));
                        }
                    }
                }
                else if (shape.LocalName == "sp")
                {
                    var raw = TextBody(Child(shape, "txBody", NsP));
                    if (raw.Trim().Length == 0) continue;
                    var content = SpecText.Clean(raw);
                    if (title.Length == 0 && content.Length > 0 && !PageCounter.IsMatch(content))
                        title = SpecExtractor.Trunc(content.Split('\n')[0], 80);
                    foreach (var line in content.Split('\n'))
                    {
                        rowNo++;
                        texts.Add(new Block("slide", slideNo, "", rowNo, 0, [line]));
                    }
                }
            }
            foreach (var b in texts) b.UnitLabel = title;
            blocks.AddRange(texts);
        }
        return (slideIds.Count, blocks);
    }

    private static OpenXmlElement? Child(OpenXmlElement? parent, string local, string ns) =>
        parent?.ChildElements.FirstOrDefault(e => e.LocalName == local && e.NamespaceUri == ns);

    private static OpenXmlElement? TableOf(OpenXmlElement graphicFrame)
    {
        var data = Child(Child(graphicFrame, "graphic", NsA), "graphicData", NsA);
        return data is not null && data.GetAttribute("uri", "").Value == TableUri ? Child(data, "tbl", NsA) : null;
    }

    private static bool IsTrue(OpenXmlElement e, string attr)
    {
        var v = e.GetAttributes().FirstOrDefault(a => a.LocalName == attr).Value;
        return v is "1" or "true";
    }

    private static bool IsSpanned(OpenXmlElement tc) => IsTrue(tc, "hMerge") || IsTrue(tc, "vMerge");

    /// <summary>python-pptx TextFrame.text: 문단을 \n 으로, 문단 안 a:br 은 \v, a:r·a:fld 는 글자.</summary>
    private static string TextBody(OpenXmlElement? txBody)
    {
        if (txBody is null) return "";
        var paras = txBody.ChildElements.Where(e => e.LocalName == "p" && e.NamespaceUri == NsA).Select(p =>
        {
            var sb = new StringBuilder();
            foreach (var c in p.ChildElements)
            {
                if (c.NamespaceUri != NsA) continue;
                if (c.LocalName == "br") sb.Append('\v');
                else if (c.LocalName is "r" or "fld") sb.Append(Child(c, "t", NsA)?.InnerText ?? "");
            }
            return sb.ToString();
        });
        return string.Join("\n", paras);
    }

    // ---- 도형 위치(python-pptx shape.top / left): 자기 xfrm → (자리표시자면) 레이아웃 → 마스터
    private static long? Top(OpenXmlElement e, SlideLayoutPart? layout, SlideMasterPart? master) => Pos(e, layout, master, "y");
    private static long? Left(OpenXmlElement e, SlideLayoutPart? layout, SlideMasterPart? master) => Pos(e, layout, master, "x");

    private static long? Pos(OpenXmlElement e, SlideLayoutPart? layout, SlideMasterPart? master, string axis)
    {
        var own = OwnOffset(e, axis);
        if (own is not null) return own;
        var ph = Placeholder(e);
        if (ph is null || layout is null) return null;
        var idx = PhIdx(ph);
        var lp = PlaceholdersOf(layout.SlideLayout).FirstOrDefault(x => PhIdx(Placeholder(x)!) == idx);
        if (lp is null) return null;
        var lv = OwnOffset(lp, axis);
        if (lv is not null) return lv;
        if (master is null) return null;
        var baseType = BaseType(PhType(Placeholder(lp)!));
        var mp = PlaceholdersOf(master.SlideMaster).FirstOrDefault(x => PhType(Placeholder(x)!) == baseType);
        return mp is null ? null : OwnOffset(mp, axis);
    }

    private static long? OwnOffset(OpenXmlElement e, string axis)
    {
        OpenXmlElement? xfrm = e.LocalName switch
        {
            "graphicFrame" => Child(e, "xfrm", NsP),
            "grpSp" => Child(Child(e, "grpSpPr", NsP), "xfrm", NsA),
            _ => Child(Child(e, "spPr", NsP), "xfrm", NsA),
        };
        var off = Child(xfrm, "off", NsA);
        var v = off?.GetAttributes().FirstOrDefault(a => a.LocalName == axis).Value;
        return long.TryParse(v, out var n) ? n : null;
    }

    private static OpenXmlElement? Placeholder(OpenXmlElement e)
    {
        var nv = e.ChildElements.FirstOrDefault(c => c.LocalName is "nvSpPr" or "nvGraphicFramePr" or "nvPicPr" or "nvCxnSpPr" or "nvGrpSpPr");
        return Child(Child(nv, "nvPr", NsP), "ph", NsP);
    }

    private static IEnumerable<OpenXmlElement> PlaceholdersOf(OpenXmlElement? root)
    {
        var tree = Child(Child(root, "cSld", NsP), "spTree", NsP);
        return tree is null ? [] : tree.ChildElements.Where(x => x.NamespaceUri == NsP && ShapeTags.Contains(x.LocalName) && Placeholder(x) is not null);
    }

    private static string PhIdx(OpenXmlElement ph) => ph.GetAttributes().FirstOrDefault(a => a.LocalName == "idx").Value ?? "0";
    private static string PhType(OpenXmlElement ph) => ph.GetAttributes().FirstOrDefault(a => a.LocalName == "type").Value ?? "obj";

    // python-pptx LayoutPlaceholder._base_placeholder 의 유형 대응
    private static string BaseType(string t) => t switch
    {
        "body" or "chart" or "clipArt" or "media" or "obj" or "dgm" or "pic" or "subTitle" or "tbl" => "body",
        "ctrTitle" or "title" => "title",
        _ => t,
    };

    // ================================================================ DOCX
    public static (int, List<Block>) Docx(byte[] data)
    {
        using var stream = new MemoryStream(data);
        using var doc = WordprocessingDocument.Open(stream, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        var blocks = new List<Block>();
        if (body is null) return (1, blocks);
        var rowNo = 0;
        // python-docx: document.paragraphs(본문 직속 문단 전부) 다음 document.tables(본문 직속 표 전부) — 문서 순서가 아님
        foreach (var p in body.ChildElements.Where(e => e.LocalName == "p" && e.NamespaceUri == NsW))
        {
            var text = SpecText.Clean(ParagraphText(p));
            if (text.Length == 0) continue;
            foreach (var line in text.Split('\n'))
            {
                rowNo++;
                blocks.Add(new Block("section", 1, "", rowNo, 0, [line]));
            }
        }
        var tableNo = 0;
        foreach (var tbl in body.ChildElements.Where(e => e.LocalName == "tbl" && e.NamespaceUri == NsW))
        {
            tableNo++;
            var rows = tbl.ChildElements.Where(e => e.LocalName == "tr" && e.NamespaceUri == NsW).ToList();
            for (var r = 0; r < rows.Count; r++)
            {
                var cells = new List<string>();
                OpenXmlElement? previous = null;
                foreach (var tc in RowCells(rows, r))
                {
                    cells.Add(ReferenceEquals(tc, previous) ? "" : SpecText.Clean(CellText(tc)));
                    previous = tc;
                }
                if (cells.Any(c => c.Length > 0))
                {
                    rowNo++;
                    blocks.Add(new Block("section", 1, "", rowNo, tableNo, cells));
                }
            }
        }
        return (1, blocks);
    }

    private static string WAttr(OpenXmlElement? e, string local) =>
        e?.GetAttributes().FirstOrDefault(a => a.LocalName == local && a.NamespaceUri == NsW).Value ?? "";

    private static int GridBefore(OpenXmlElement tr) =>
        int.TryParse(WAttr(Child(Child(tr, "trPr", NsW), "gridBefore", NsW), "val"), out var n) ? n : 0;

    private static int GridSpan(OpenXmlElement tc) =>
        int.TryParse(WAttr(Child(Child(tc, "tcPr", NsW), "gridSpan", NsW), "val"), out var n) && n > 0 ? n : 1;

    /// <summary>w:vMerge 가 없으면 null, val 이 없으면 "continue"(스키마 기본값).</summary>
    private static string? VMerge(OpenXmlElement tc)
    {
        var vm = Child(Child(tc, "tcPr", NsW), "vMerge", NsW);
        if (vm is null) return null;
        var v = WAttr(vm, "val");
        return v.Length == 0 ? "continue" : v;
    }

    private static List<(OpenXmlElement tc, int offset)> TcsWithOffset(OpenXmlElement tr)
    {
        var list = new List<(OpenXmlElement, int)>();
        var offset = GridBefore(tr);
        foreach (var tc in tr.ChildElements.Where(e => e.LocalName == "tc" && e.NamespaceUri == NsW))
        {
            list.Add((tc, offset));
            offset += GridSpan(tc);
        }
        return list;
    }

    /// <summary>python-docx _Row.cells: 가로 병합은 같은 셀을 칸 수만큼, 세로 병합 이어짐은 위 행의 같은 칸 셀.</summary>
    private static IEnumerable<OpenXmlElement> RowCells(List<OpenXmlElement> rows, int r)
    {
        foreach (var (tc, offset) in TcsWithOffset(rows[r]))
        {
            var root = tc;
            var rr = r;
            var off = offset;
            while (VMerge(root) == "continue" && rr > 0)
            {
                rr--;
                var above = TcsWithOffset(rows[rr]).FirstOrDefault(x => x.offset == off);
                if (above.tc is null) break;
                root = above.tc;
            }
            for (var k = 0; k < GridSpan(root); k++) yield return root;
        }
    }

    private static string CellText(OpenXmlElement tc) =>
        string.Join("\n", tc.ChildElements.Where(e => e.LocalName == "p" && e.NamespaceUri == NsW).Select(ParagraphText));

    /// <summary>python-docx Paragraph.text: 직속 w:r 과 w:hyperlink(안의 w:r) 글자.</summary>
    private static string ParagraphText(OpenXmlElement p)
    {
        var sb = new StringBuilder();
        foreach (var c in p.ChildElements)
        {
            if (c.NamespaceUri != NsW) continue;
            if (c.LocalName == "r") AppendRun(sb, c);
            else if (c.LocalName == "hyperlink")
                foreach (var r in c.ChildElements.Where(e => e.LocalName == "r" && e.NamespaceUri == NsW)) AppendRun(sb, r);
        }
        return sb.ToString();
    }

    private static void AppendRun(StringBuilder sb, OpenXmlElement run)
    {
        foreach (var e in run.ChildElements)
        {
            if (e.NamespaceUri != NsW) continue;
            switch (e.LocalName)
            {
                case "t": sb.Append(e.InnerText); break;
                case "tab" or "ptab": sb.Append('\t'); break;
                case "cr": sb.Append('\n'); break;
                case "noBreakHyphen": sb.Append('-'); break;
                case "br":
                    var type = WAttr(e, "type");
                    if (type.Length == 0 || type == "textWrapping") sb.Append('\n');
                    break;
            }
        }
    }
}
