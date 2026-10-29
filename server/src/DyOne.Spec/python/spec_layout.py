"""사양서 PDF에서 레이아웃(전체 시스템 구성도·부스 레이아웃) 그림 자리를 찾는다 — 사양정리 2장 표 위에 붙일 그림.
사용: python spec_layout.py <pdf 경로>  → {"page": n, "clip": [x0, y0, x1, y1]} (못 찾으면 {})
찾는 법(영업부 사양정리: 광주2는 사양서 2쪽 'L A Y O U T (전체 시스템 구성도)', 화성4는 5쪽 '레이아웃 중상도 부스 레이아웃'):
 1) 앞 12쪽에서 'LAYOUT·레이아웃·구성도·배치도' 글이 있는 쪽(목차·문장 속 언급이 아니라 짧은 제목 줄)
 2) 그 제목 아래부터, 다음 번호 항목(예: '4 일정')이나 쪽 끝 전까지에서 그림·선(도면)이 있는 영역
"""
import json
import re
import sys
import fitz

doc = fitz.open(sys.argv[1])
# 둘째 인자: layout(기본, 표 위 그림) | schedule(3장 Master schedule — 사양서 일정표·간트)
KIND = sys.argv[2] if len(sys.argv) > 2 else "layout"
KEY = re.compile(r"LAYOUT|레이아웃|구성도|배치도", re.I) if KIND == "layout" else     re.compile(r"일정\s*계획|추진\s*일정|상세\s*일정|공사\s*일정|일정표|MASTER\s*SCHEDULE|PROJECT\s*SCHEDULE|TIMING\s*PLAN|^\s*(?:\d+\s*)?일정\s*$|SCHEDULE", re.I)


def norm(t):
    return re.sub(r"\s+", "", t)


def find():
    # 레이아웃은 앞 12쪽, 일정표는 뒤 첨부(아산 32쪽 '첨부#1 상세일정')까지 — 1쪽(표지·목차의 '7. 공사일정')은 빼고
    for pno in (range(min(12, len(doc))) if KIND == "layout" else range(1, len(doc))):
        page = doc[pno]
        W, H = page.rect.width, page.rect.height
        lines = []
        for b in page.get_text("dict")["blocks"]:
            for ln in b.get("lines", []):
                text = "".join(s["text"] for s in ln["spans"])
                if text.strip():
                    lines.append((fitz.Rect(ln["bbox"]), text))
        heads = [(r, t) for r, t in lines if (KEY.search(norm(t)) or KEY.search(t.strip())) and len(norm(t)) <= 30 and not re.search(r"참조|따른다|별첨|제출|변동|협의", t)
                 # 비고 칸의 참고 표시("※ 상세일정")는 제목이 아님 — 쪽 왼쪽 절반의 제목만
                 and not t.strip().startswith("※") and r.x0 < W * 0.5]
        if not heads:
            continue
        head = min(heads, key=lambda x: x[0].y0)[0]
        top = head.y0 - 2
        # 표의 가로 테두리(긴 선): 다음 항목 번호는 행 가운데에 있어서, 번호 바로 위의 테두리가 그 항목 행의 시작
        rules = [d["rect"].y0 for d in page.get_drawings() if d["rect"].height < 2.5 and d["rect"].width > W * 0.45]
        # 일정표 제목이 왼쪽 항목 칸('4 일정', 화성4 5쪽)이면 글자가 행 가운데라 간트가 제목 위에서 시작 — 그 행의 위 테두리부터
        if KIND == "schedule" and head.x0 < W * 0.2:
            above = [y for y in rules if y < head.y0 - 2]
            if above:
                top = max(above)
        # 그림 영역: 제목(행) 아래의 이미지·벡터 도면
        shapes = [fitz.Rect(i["bbox"]) for i in page.get_image_info()]
        shapes += [d["rect"] for d in page.get_drawings() if d["rect"].width < W * 0.95 or d["rect"].height < 3]
        shapes = [r for r in shapes if r.y1 > max(head.y1, top + 20) and (KIND == "layout" or r.y0 >= top - 15) and r.width * r.height > 4]
        # 다음 번호 항목(왼쪽 칸의 '4', '5' …)이 나오면 그 위까지
        bottom = H - 20
        for r, t in lines:
            # 일정표는 간트 표 안의 순번(1, 2, 3 …)을 다음 항목으로 보지 않는다 — 쪽 끝까지
            if KIND == "layout" and r.y0 > head.y1 + 40 and r.x0 < W * 0.2 and re.fullmatch(r"\s*\d{1,2}\s*", t):
                above = [y for y in rules if head.y1 + 20 < y < r.y0]
                bottom = min(bottom, (max(above) if above else r.y0) - 2)
        body = [r for r in shapes if r.y0 < bottom and r.height < H * 0.9]
        # 표 테두리 같은 아주 넓고 얇은 선만 있으면 그림이 아님 — 실제 그림(이미지) 또는 작은 선이 많을 때
        imgs = [r for r in body if r.width > 40 and r.height > 40]
        if not imgs and len(body) < 30:
            continue
        # 일정표 그림이 행 테두리에 살짝 걸쳐 시작하면(화성4 376 < 383.5) 그림 윗변부터
        if KIND == "schedule":
            top = min(top, min(r.y0 for r in body) - 1)
        area = fitz.Rect(min(r.x0 for r in body), top, max(r.x1 for r in body), min(bottom, max(r.y1 for r in body)))
        area &= page.rect
        if area.height < 60 or area.width < 150:
            continue
        return {"page": pno + 1, "clip": [round(area.x0, 1), round(area.y0, 1), round(area.x1, 1), round(area.y1, 1)]}
    return {}


print(json.dumps(find()))
