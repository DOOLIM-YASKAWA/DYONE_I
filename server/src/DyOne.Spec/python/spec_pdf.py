"""PDF → blocks (stdin: PDF bytes, stdout: JSON).

DY ONE(dy-one-internal) 사양서 분석의 PDF 추출 도우미. PDF 표 인식은 PyMuPDF(find_tables)가 .NET 라이브러리보다
월등해서 이 부분만 Python 으로 둔다. 규칙은 dyone-pilot server/spec_extract.py 의 _pdf / clean 과 같다.
원본은 읽기만 한다(표준입력으로 받은 바이트만 다룸, 파일을 쓰지 않음).
"""
from __future__ import annotations

import json
import re
import sys

WHITESPACE = re.compile(r"[ \t 　]+")
HANGUL = re.compile(r"[가-힣]")
LATIN_SUPPLEMENT = re.compile(r"[¡-ÿ]")


def repair_mojibake(text: str) -> str:
    if not text or len(LATIN_SUPPLEMENT.findall(text)) / len(text) < 0.10:
        return text
    try:
        candidate = text.encode("latin-1").decode("cp949")
    except (UnicodeEncodeError, UnicodeDecodeError):
        return text
    if not candidate or len(HANGUL.findall(candidate)) / len(candidate) < 0.20:
        return text
    return candidate


def clean(value: object) -> str:
    if value is None:
        return ""
    text = repair_mojibake(str(value))
    text = text.replace("\r\n", "\n").replace("\r", "\n").replace("\x0b", "\n")
    lines = [WHITESPACE.sub(" ", line).strip() for line in text.split("\n")]
    return "\n".join(line for line in lines if line)


def extract(data: bytes) -> dict:
    import fitz

    blocks: list[dict] = []
    with fitz.open(stream=data, filetype="pdf") as doc:
        for page_no, page in enumerate(doc, start=1):
            try:
                tables = list(page.find_tables().tables)
            except Exception:  # noqa: BLE001 — a broken page keeps its text lines
                tables = []
            boxes = [fitz.Rect(t.bbox) for t in tables]
            row_no = 0
            items: list[tuple[float, float, list[str], int]] = []
            for t_index, table in enumerate(tables, start=1):
                rows = table.extract() or []
                cells_rows = getattr(table, "rows", [])
                for r_index, row in enumerate(rows):
                    y = cells_rows[r_index].bbox[1] if r_index < len(cells_rows) else table.bbox[1]
                    items.append((y, table.bbox[0], [clean(c) for c in row], t_index))
            for x0, y0, x1, y1, text, *_ in page.get_text("blocks"):
                rect = fitz.Rect(x0, y0, x1, y1)
                if any(rect.intersects(b) and (rect & b).get_area() > 0.5 * max(rect.get_area(), 1) for b in boxes):
                    continue
                for line in clean(text).split("\n"):
                    if line:
                        items.append((y0, x0, [line], 0))
            items.sort(key=lambda it: (round(it[0], 1), it[1]))
            for _y, _x, cells, t_index in items:
                if any(cells):
                    row_no += 1
                    blocks.append({"unit_index": page_no, "row": row_no, "table": t_index, "cells": cells})
        return {"units": doc.page_count, "blocks": blocks}


if __name__ == "__main__":
    import contextlib

    data = sys.stdin.buffer.read()
    # PyMuPDF 가 표 인식 중 안내 문구를 표준출력에 찍는다 → 결과 JSON 이 깨지지 않게 그동안은 표준오류로 돌린다.
    with contextlib.redirect_stdout(sys.stderr):
        result = extract(data)
    sys.stdout.buffer.write(json.dumps(result, ensure_ascii=False).encode("utf-8"))
    sys.stdout.buffer.flush()
