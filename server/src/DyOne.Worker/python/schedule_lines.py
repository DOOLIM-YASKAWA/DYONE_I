# 프로젝트 폴더의 사양정리·수행계획서·사양서에서 '일정 줄'만 뽑는다 — 읽기 전용(파일을 열어 글만 읽고, 고치지 않는다).
# Worker(project-main 잡)가 부른다: python schedule_lines.py <입력.json> <출력.json> [그림폴더]
#   입력: {"DLP-26-066": "Q:/01_프로젝트/.../DLP-26-066 [...]", ...}
#   출력: {"DLP-26-066": [{"file": "...", "path": "...", "kind": "summary|plan|spec", "mtime": 1730000000.0, "loc": "p2", "line": "..."}]}
# 일정 줄 = 단계 낱말(OLT·선적·설치·시운전·SOP·양산 …)과 날짜 모양이 함께 있는 줄. 날짜로 바꾸는 일은 Worker(사내 AI + 숫자 검사)가 한다.
# 그림 일정표(11-04): '일정·Schedule' 제목이 있는데 날짜 글자가 거의 없고 그림이 있는 쪽(PDF) · 슬라이드(pptx)를
#   그림 파일로 만들어 그림폴더에 저장하고 {"png": 경로}로 알린다. 그림은 일정표 부분만 크게(작으면 쪽 전체를 크게).
import json
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')

KEY = re.compile(r'OLT|선적|출하|납기|설치|시운전|커미셔닝|Commission|Installation|SOP|S\.O\.P|양산|Mass\s*Production|시생산|품확|Pre-?production|Shipping|Line-?up', re.I)
DATE = re.compile(r"(20\d{2}|'?’?\d{2})\s*[./년-]\s*\d{1,2}|\d{1,2}\s*월|\d{1,2}/\d{1,2}", re.I)
SUMMARY = re.compile(r'사양\s*정리|사양정리|summary', re.I)
SUBDIRS = (('3. 수행 계획서', 'plan'), ('2. 사양서', 'spec'))
HEAD = re.compile(r'일\s*정|Schedule|SCHEDULE|Master\s*Plan|공정표|Time\s*line', re.I)
MAX_LINES_PER_FILE = 20
MAX_IMAGES_PER_PROJECT = 4
IMG_WIDTH = 1800
MAX_IMG_BYTES = 700 * 1024  # 사내 AI 서버가 받는 크기(넘으면 413) — JPEG로 줄인다
MAX_BYTES = 120 * 1024 * 1024


def lines_pdf(f):
    try:
        import fitz  # PyMuPDF (DyOne.Spec과 같은 도구)
        with fitz.open(f) as doc:
            for i, pg in enumerate(doc, 1):
                if i > 60:
                    break
                for ln in pg.get_text().splitlines():
                    yield f'p{i}', ln
        return
    except ImportError:
        pass
    from pypdf import PdfReader
    for i, pg in enumerate(PdfReader(f).pages[:60], 1):
        for ln in (pg.extract_text() or '').splitlines():
            yield f'p{i}', ln


def lines_pptx(f):
    from pptx import Presentation
    for i, s in enumerate(Presentation(f).slides, 1):
        for sh in s.shapes:
            if sh.has_text_frame:
                for ln in sh.text_frame.text.splitlines():
                    yield f'p{i}', ln
            if getattr(sh, 'has_table', False) and sh.has_table:
                for r in sh.table.rows:
                    yield f'p{i}', ' | '.join(c.text.replace('\n', ' ') for c in r.cells)


def lines_xlsx(f):
    import openpyxl
    wb = openpyxl.load_workbook(f, read_only=True, data_only=True)
    try:
        for ws in wb.worksheets[:8]:
            for row in ws.iter_rows(max_row=300, values_only=True):
                vals = [str(v) for v in row if v is not None]
                if vals:
                    yield ws.title, ' | '.join(vals)
    finally:
        wb.close()


def images_pdf(f, outdir, tag):
    """일정 제목이 있고 날짜 글자가 3개 이하이며 그림이 있는 쪽 → 그림 부분(또는 쪽 전체)을 크게 저장"""
    import fitz
    out = []
    with fitz.open(f) as doc:
        for i, pg in enumerate(doc, 1):
            if i > 60:
                break
            t = pg.get_text()
            imgs = pg.get_images(full=True)
            if not HEAD.search(t) or not imgs or len(DATE.findall(t)) > 3:
                continue
            area = pg.rect.width * pg.rect.height
            rects = [r for im in imgs for r in pg.get_image_rects(im[0]) if r.width * r.height > area * 0.04]
            clip = pg.rect
            if rects:
                u = rects[0]
                for r in rects[1:]:
                    u = u | r
                if u.width * u.height >= area * 0.08:
                    clip = (u + (-12, -40, 12, 30)) & pg.rect  # 위쪽 머리줄(연·월)이 잘리지 않게 조금 넓힌다
            zoom = max(1.5, min(5.0, IMG_WIDTH / max(clip.width, 1)))
            png = os.path.join(outdir, f'{tag}_{len(out)}_{i}.png')
            pg.get_pixmap(matrix=fitz.Matrix(zoom, zoom), clip=clip).save(png)
            out.append((f'p{i}', png))
    return out


def images_pptx(f, outdir, tag):
    """일정 제목이 있는 슬라이드의 가장 큰 그림을 저장(날짜 글자가 많으면 글자 읽기로 충분하니 건너뜀)"""
    from pptx import Presentation
    from pptx.enum.shapes import MSO_SHAPE_TYPE
    out = []
    for i, sl in enumerate(Presentation(f).slides, 1):
        text = ' '.join(sh.text_frame.text for sh in sl.shapes if sh.has_text_frame)
        if not HEAD.search(text) or len(DATE.findall(text)) > 3:
            continue
        pics = [sh for sh in sl.shapes if sh.shape_type == MSO_SHAPE_TYPE.PICTURE]
        if not pics:
            continue
        big = max(pics, key=lambda sh: (sh.width or 0) * (sh.height or 0))
        png = os.path.join(outdir, f'{tag}_{len(out)}_{i}.{big.image.ext}')
        open(png, 'wb').write(big.image.blob)
        out.append((f'p{i}', png))
    return out


def shrink(path):
    """그림을 JPEG로, 가로 1800px·700KB 안으로 줄인다(사내 AI 서버 요청 크기 한도)"""
    import fitz
    pix = fitz.Pixmap(path)
    if pix.alpha:
        pix = fitz.Pixmap(pix, 0)
    if pix.n > 3:
        pix = fitz.Pixmap(fitz.csRGB, pix)
    while pix.width > IMG_WIDTH * 1.5:
        pix.shrink(1)  # 가로·세로 절반
    out = os.path.splitext(path)[0] + '.jpg'
    for q in (85, 70, 55):
        pix.save(out, jpg_quality=q)
        if os.path.getsize(out) <= MAX_IMG_BYTES:
            break
    if out != path:
        try:
            os.remove(path)
        except OSError:
            pass
    return out


IMAGERS = {'pdf': images_pdf, 'pptx': images_pptx}


def lp(f):
    """윈도 긴 경로(260자 넘음)도 열리게 앞에 \\\\?\\ 를 붙인다"""
    if os.name == 'nt':
        prefix = '\\\\?\\'
        a = os.path.abspath(f)
        return a if a.startswith(prefix) else prefix + a
    return f


VER = re.compile(r'(?i)[\s_\-]*(v|ver|rev|r)\.?\s*\d+(\.\d+)?|\(.*?\)|\[.*?\]|\d{6,8}|_\d+$')


def base_name(fn):
    """같은 문서의 여러 판(V10·V13·rev4·rev10·날짜)을 하나로 묶는 이름"""
    return re.sub(r'\s+', ' ', VER.sub('', fn.rsplit('.', 1)[0])).strip().lower()


READERS = {'pdf': lines_pdf, 'pptx': lines_pptx, 'xlsx': lines_xlsx, 'xlsm': lines_xlsx}


def scan(root, outdir=None, tag='x'):
    hits = []
    images = []
    for sub, kind in SUBDIRS:
        d = os.path.join(root, sub)
        if not os.path.isdir(d):
            continue
        for dp, _, fs in os.walk(d):
            for fn in fs:
                ext = fn.lower().rsplit('.', 1)[-1] if '.' in fn else ''
                if ext not in READERS or fn.startswith('~$'):
                    continue
                f = os.path.join(dp, fn)
                try:
                    st = os.stat(f)
                    if st.st_size > MAX_BYTES:
                        continue
                    k = 'summary' if SUMMARY.search(fn) else kind
                    n = 0
                    seen = set()
                    for loc, ln in READERS[ext](lp(f)):
                        ln = re.sub(r'\s+', ' ', ln).strip()
                        if len(ln) < 4 or len(ln) > 300 or ln in seen:
                            continue
                        if KEY.search(ln) and DATE.search(ln):
                            seen.add(ln)
                            hits.append({'file': fn, 'path': f, 'kind': k, 'mtime': st.st_mtime, 'loc': loc, 'line': ln})
                            n += 1
                            if n >= MAX_LINES_PER_FILE:
                                break
                    if outdir and ext in IMAGERS:
                        for loc, png in IMAGERS[ext](lp(f), outdir, f'{tag}_{len(images)}'):
                            png = shrink(png)
                            images.append({'file': fn, 'path': f, 'kind': k, 'mtime': st.st_mtime, 'loc': loc, 'line': '', 'png': png})
                except Exception as e:  # 못 읽는 파일은 건너뛴다(암호·손상 등)
                    hits.append({'file': fn, 'path': f, 'kind': 'error', 'mtime': 0, 'loc': '', 'line': str(e)[:120]})
    # 그림은 사양정리 › 수행계획서 › 사양서, 새 파일 순으로 몇 장만(무거우므로)
    rank = {'summary': 0, 'plan': 1, 'spec': 2}
    images.sort(key=lambda x: (rank.get(x['kind'], 3), -x['mtime']))
    keep, seen_doc, drop = [], set(), []
    for x in images:  # 같은 문서의 옛 판은 빼고 가장 새 판만(같은 쪽 번호 기준)
        k = (base_name(x['file']), x['loc'])
        if k in seen_doc or len(keep) >= MAX_IMAGES_PER_PROJECT:
            drop.append(x)
            continue
        seen_doc.add(k)
        keep.append(x)
    for x in drop:
        try:
            os.remove(x['png'])
        except OSError:
            pass
    return hits + keep


def main():
    dirs = json.load(open(sys.argv[1], encoding='utf-8'))
    outdir = sys.argv[3] if len(sys.argv) > 3 else None
    if outdir:
        os.makedirs(outdir, exist_ok=True)
    out = {code: scan(root, outdir, f'p{n}') for n, (code, root) in enumerate(dirs.items())}
    json.dump(out, open(sys.argv[2], 'w', encoding='utf-8'), ensure_ascii=False)
    print('projects', len(out), 'lines', sum(1 for v in out.values() for x in v if not x.get('png')), 'images', sum(1 for v in out.values() for x in v if x.get('png')))


if __name__ == '__main__':
    main()
