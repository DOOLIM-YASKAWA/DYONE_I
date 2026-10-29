"""PDF가 아닌 사양서(pptx·ppt·xlsx·xlsm·xls·docx·doc)를 PDF로 바꾼다 — 화면 쪽 그림·하이라이트·초안을 PDF와 같은 모양으로.
사용: python spec_topdf.py <원본 경로> <PDF 경로> [soffice 경로]
1) Microsoft Office(COM, pywin32)가 있으면 그것으로(서식이 가장 같다)
2) 없으면 LibreOffice(soffice --headless --convert-to pdf)
둘 다 안 되면 종료 코드 3. 원본은 읽기만 한다.
"""
import os
import shutil
import subprocess
import sys
import tempfile

src, out = os.path.abspath(sys.argv[1]), os.path.abspath(sys.argv[2])
soffice = sys.argv[3] if len(sys.argv) > 3 and sys.argv[3] else None
ext = os.path.splitext(src)[1].lower()


def by_office():
    import pythoncom
    import win32com.client
    pythoncom.CoInitialize()
    try:
        if ext in (".pptx", ".ppt"):
            app = win32com.client.DispatchEx("PowerPoint.Application")
            try:
                pres = app.Presentations.Open(src, True, False, False)  # ReadOnly, Untitled, WithWindow
                pres.SaveAs(out, 32)  # ppSaveAsPDF
                pres.Close()
            finally:
                app.Quit()
        elif ext in (".xlsx", ".xlsm", ".xls"):
            app = win32com.client.DispatchEx("Excel.Application")
            app.Visible = False
            app.DisplayAlerts = False
            try:
                wb = app.Workbooks.Open(src, 0, True)  # UpdateLinks=0, ReadOnly
                wb.ExportAsFixedFormat(0, out)  # xlTypePDF
                wb.Close(False)
            finally:
                app.Quit()
        elif ext in (".docx", ".doc"):
            app = win32com.client.DispatchEx("Word.Application")
            app.Visible = False
            try:
                doc = app.Documents.Open(src, False, True)  # ConfirmConversions, ReadOnly
                doc.ExportAsFixedFormat(out, 17)  # wdExportFormatPDF
                doc.Close(False)
            finally:
                app.Quit()
        else:
            return False
        return os.path.exists(out)
    finally:
        pythoncom.CoUninitialize()


def by_libreoffice():
    exe = soffice or shutil.which("soffice") or shutil.which("soffice.exe")
    if not exe:
        return False
    tmp = tempfile.mkdtemp(prefix="spec-pdf-")
    try:
        subprocess.run([exe, "--headless", "--convert-to", "pdf", "--outdir", tmp, src], check=True, timeout=180,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        made = os.path.join(tmp, os.path.splitext(os.path.basename(src))[0] + ".pdf")
        if not os.path.exists(made):
            return False
        shutil.move(made, out)
        return True
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


for way in (by_office, by_libreoffice):
    try:
        if way():
            sys.exit(0)
    except Exception as e:  # 다음 방법으로
        print(f"{way.__name__}: {e}", file=sys.stderr)
sys.exit(3)
