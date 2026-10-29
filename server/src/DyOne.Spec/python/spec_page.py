"""사양서 PDF 한 쪽을 PNG로(화면 근거 패널에서 원문 쪽 보기).
사용: python spec_page.py <pdf 경로> <쪽 번호(1부터)> [dpi] [표시할 글(JSON 배열)]  → PNG를 표준 출력으로
표시할 글: 초안 칸 값·원문 조각. 쪽에서 그대로 찾고, 없으면 표 칸·구분자로 나눈 조각, 그래도 없으면 숫자·영문이 든 낱말
(10,000(L), 66.6UPH, PPH707)로 찾아 노란색으로 칠한다.
"""
import json
import re
import sys
import fitz

path, page = sys.argv[1], int(sys.argv[2])
dpi = int(sys.argv[3]) if len(sys.argv) > 3 else 110
marks = json.loads(sys.argv[4]) if len(sys.argv) > 4 and sys.argv[4] else []
doc = fitz.open(path)
if page < 1 or page > len(doc):
    sys.exit(2)
p = doc[page - 1]


def pieces(mark):
    """한 표시 글 → 찾을 조각들(큰 것부터)"""
    mark = re.sub(r"\*\*|\{\{|\}\}|\[\[|\]\]|<<|>>|…", " ", mark).strip()
    yield [mark]
    yield [s.strip(" .()") for s in re.split(r"\s*(?:\||/|⏎|\n|←|:|：|,(?!\d))\s*", mark) if len(s.strip(" .()")) >= 4]
    yield [t for t in re.findall(r"[0-9A-Za-z][0-9A-Za-z.,~±%()/-]*[0-9A-Za-z)%]", mark)
           if len(t) >= 3 and (re.search(r"\d", t) or re.fullmatch(r"[A-Z]{3,}", t))]


rects = []
for m in marks[:40]:
    for group in pieces(m):
        hits = [r for s in group if s for r in p.search_for(s)[:6]]
        if hits:
            rects.extend(hits)
            break  # 큰 조각으로 찾았으면 더 잘게 찾지 않는다
for r in rects[:80]:
    annot = p.add_highlight_annot(r)
    annot.set_colors(stroke=(1, 0.85, 0))
    annot.update()
# 다섯째 인자: 잘라 낼 영역 [x0, y0, x1, y1](표 위 레이아웃 그림)
clip = json.loads(sys.argv[5]) if len(sys.argv) > 5 and sys.argv[5] else None
sys.stdout.buffer.write(p.get_pixmap(dpi=dpi, annots=True, clip=fitz.Rect(*clip) if clip else None).tobytes("png"))
