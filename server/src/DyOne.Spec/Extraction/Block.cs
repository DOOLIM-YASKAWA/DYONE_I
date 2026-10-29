namespace DyOne.Spec.Extraction;

/// <summary>
/// 원문의 표 한 행 또는 글 한 줄 + 원문 위치(쪽·슬라이드·시트·행).
/// dyone-pilot server/spec_extract.py 의 Block 을 그대로 옮겼다(조항이 원문 위치를 가리키게 하려는 것).
/// </summary>
public sealed class Block(string unitKind, int unitIndex, string unitLabel, int row, int table, List<string> cells)
{
    /// <summary>page | slide | sheet | section</summary>
    public string UnitKind { get; } = unitKind;
    /// <summary>1부터 시작하는 쪽/슬라이드/시트 번호</summary>
    public int UnitIndex { get; } = unitIndex;
    /// <summary>시트 이름 또는 슬라이드 제목</summary>
    public string UnitLabel { get; set; } = unitLabel;
    /// <summary>단위 안에서 1부터 시작하는 행(표 행 또는 줄 번호)</summary>
    public int Row { get; } = row;
    /// <summary>0이면 일반 글, 아니면 단위 안 표 번호(1부터)</summary>
    public int Table { get; } = table;
    public List<string> Cells { get; } = cells;

    public string Text => string.Join(" | ", Cells.Where(c => c.Length > 0));
}

public sealed record Extraction(string FileName, string Suffix, int Units, List<Block> Blocks)
{
    /// <summary>PDF 추출에 쓴 방식(python=PyMuPDF 표 인식, pdfpig=.NET 대체). 그 외 형식은 null.</summary>
    public string? PdfEngine { get; init; }
}
