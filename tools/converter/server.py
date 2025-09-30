# server.py
import os, tempfile, shutil, glob, json, subprocess, time, zipfile
from xml.etree import ElementTree as ET
from flask import Flask, request, send_file, abort
from pypdf import PdfReader, PdfWriter

P_NS = "http://schemas.openxmlformats.org/presentationml/2006/main"
R_NS = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
REL_NS = "http://schemas.openxmlformats.org/package/2006/relationships"
APP_NS = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties"
VT_NS = "http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes"

ET.register_namespace("p", P_NS)
ET.register_namespace("a", "http://schemas.openxmlformats.org/drawingml/2006/main")
ET.register_namespace("r", R_NS)
ET.register_namespace("ep", APP_NS)
ET.register_namespace("vt", VT_NS)


def _extract_single_slide_pptx(src_path, slide_index, dst_path):
    if slide_index < 1:
        raise ValueError("slide_index must be >= 1")

    with tempfile.TemporaryDirectory(prefix="split_") as unzip_dir:
        with zipfile.ZipFile(src_path) as zf:
            zf.extractall(unzip_dir)

        pres_xml_path = os.path.join(unzip_dir, "ppt", "presentation.xml")
        if not os.path.exists(pres_xml_path):
            raise ValueError("Invalid PPTX structure: missing presentation.xml")

        pres_tree = ET.parse(pres_xml_path)
        pres_root = pres_tree.getroot()
        ns = {"p": P_NS, "r": R_NS}
        sld_id_list = pres_root.find("p:sldIdLst", ns)
        if sld_id_list is None:
            raise ValueError("Invalid PPTX structure: missing slide list")

        sld_ids = sld_id_list.findall("p:sldId", ns)
        if slide_index > len(sld_ids):
            raise ValueError("Requested slide is out of range")

        target_sld = sld_ids[slide_index - 1]
        target_rid = target_sld.attrib.get(f"{{{R_NS}}}id")
        if not target_rid:
            raise ValueError("Slide entry missing relationship id")

        for sld in list(sld_ids):
            if sld is not target_sld:
                sld_id_list.remove(sld)

        pres_tree.write(pres_xml_path, xml_declaration=True, encoding="UTF-8")

        pres_rels_path = os.path.join(unzip_dir, "ppt", "_rels", "presentation.xml.rels")
        if not os.path.exists(pres_rels_path):
            raise ValueError("Invalid PPTX structure: missing presentation relationships")

        rels_tree = ET.parse(pres_rels_path)
        rels_root = rels_tree.getroot()
        keep_target = None
        for rel in list(rels_root):
            if rel.attrib.get("Id") == target_rid:
                keep_target = rel.attrib.get("Target")
            else:
                rels_root.remove(rel)

        if not keep_target:
            raise ValueError("Slide relationship target not found")

        rels_tree.write(pres_rels_path, xml_declaration=True, encoding="UTF-8")

        slide_dir = os.path.join(unzip_dir, "ppt", "slides")
        rel_dir = os.path.join(slide_dir, "_rels")
        keep_slide_name = os.path.basename(keep_target)
        keep_rel_name = keep_slide_name + ".rels"

        if os.path.isdir(slide_dir):
            for name in os.listdir(slide_dir):
                if not name.endswith(".xml"):
                    continue
                if name != keep_slide_name:
                    os.remove(os.path.join(slide_dir, name))

        if os.path.isdir(rel_dir):
            for name in os.listdir(rel_dir):
                if not name.endswith(".rels"):
                    continue
                if name != keep_rel_name:
                    os.remove(os.path.join(rel_dir, name))

        app_xml_path = os.path.join(unzip_dir, "docProps", "app.xml")
        if os.path.exists(app_xml_path):
            app_tree = ET.parse(app_xml_path)
            app_root = app_tree.getroot()
            ns_app = {"ep": APP_NS, "vt": VT_NS}
            slides_elem = app_root.find("ep:Slides", ns_app)
            if slides_elem is not None:
                slides_elem.text = "1"
            nodes_elem = app_root.find("ep:Notes", ns_app)
            if nodes_elem is not None:
                nodes_elem.text = "0"
            titles_vec = app_root.find("ep:TitlesOfParts/vt:vector", ns_app)
            if titles_vec is not None:
                entries = titles_vec.findall("vt:lpstr", ns_app)
                if entries:
                    keep_title = entries[min(slide_index - 1, len(entries) - 1)]
                    for entry in list(entries):
                        if entry is not keep_title:
                            titles_vec.remove(entry)
                    titles_vec.set("size", "1")
            app_tree.write(app_xml_path, xml_declaration=True, encoding="UTF-8")

        with zipfile.ZipFile(dst_path, "w", compression=zipfile.ZIP_DEFLATED) as out_zip:
            for root_dir, _, files in os.walk(unzip_dir):
                for filename in files:
                    full_path = os.path.join(root_dir, filename)
                    arcname = os.path.relpath(full_path, unzip_dir)
                    out_zip.write(full_path, arcname)


def create_app():
    app = Flask(__name__)
    SOFFICE = "soffice"

    def run(cmd):
        r = subprocess.run(cmd, capture_output=True, text=True)
        if r.returncode != 0:
            raise RuntimeError(
                f"CMD failed: {' '.join(cmd)}\nSTDERR:\n{r.stderr}\nSTDOUT:\n{r.stdout}"
            )
        return r

    @app.post("/export")
    def export():
        """
        POST /export?fmt=pdf&slide=1|all
        Body: multipart/form-data with field 'file' = pptx
        """
        if "file" not in request.files:
            abort(400, "Upload PPTX in 'file' field")

        fmt = (request.args.get("fmt") or "pdf").lower()
        slide_arg = request.args.get("slide")
        slide = None
        if slide_arg:
            normalized = slide_arg.strip().lower()
            if normalized in ("all", "deck", "full"):
                slide = None
            else:
                try:
                    slide = int(slide_arg)
                except ValueError:
                    abort(400, "slide must be an integer or 'all'")
                if slide < 1:
                    abort(400, "slide must be >= 1")
        if fmt != "pdf":
            abort(400, "fmt must be pdf")

        start = time.monotonic()

        tmpdir = tempfile.mkdtemp(prefix="lo_")
        try:
            in_path = os.path.join(tmpdir, "in.pptx")
            request.files["file"].save(in_path)

            outdir = os.path.join(tmpdir, "out")
            os.makedirs(outdir, exist_ok=True)

            slide_width_pt, slide_height_pt = _read_slide_size(in_path)

            work_pptx = in_path
            used_split = False
            if slide:
                split_path = os.path.join(tmpdir, "single.pptx")
                try:
                    _extract_single_slide_pptx(in_path, slide, split_path)
                    work_pptx = split_path
                    used_split = True
                except Exception:
                    work_pptx = in_path
                    used_split = False

            filter_name = "impress_pdf_Export"
            if slide is None or used_split:
                filter_str = f"pdf:{filter_name}"
            else:
                filter_dict = {"PageRange": {"type": "string", "value": f"{slide}-{slide}"}}
                filter_str = f"pdf:{filter_name}:{json.dumps(filter_dict)}"

            cmd = [
                SOFFICE, "--headless", "--invisible",
                "--convert-to", filter_str,
                "--outdir", outdir, work_pptx
            ]
            run(cmd)

            pdfs = sorted(glob.glob(os.path.join(outdir, "*.pdf")))
            if not pdfs:
                abort(500, "No PDF output")

            for pdf_path in pdfs:
                _normalize_pdf(pdf_path, slide_width_pt, slide_height_pt)

            resp = send_file(pdfs[-1], mimetype="application/pdf")
            if used_split:
                resp.headers["X-Split-Deck"] = "true"
            elapsed_ms = (time.monotonic() - start) * 1000
            resp.headers["X-Convert-Time"] = f"{elapsed_ms:.2f}ms"
            return resp

        except RuntimeError as e:
            abort(500, str(e))
        finally:
            shutil.rmtree(tmpdir, ignore_errors=True)

    return app


EMU_PER_POINT = 12700


def _read_slide_size(pptx_path: str) -> tuple[float, float]:
    with zipfile.ZipFile(pptx_path) as zf:
        with zf.open("ppt/presentation.xml") as f:
            tree = ET.parse(f)
    pres = tree.getroot()
    sld_sz = pres.find("{http://schemas.openxmlformats.org/presentationml/2006/main}sldSz")
    if sld_sz is None:
        return 960.0, 540.0
    cx = int(sld_sz.attrib.get("cx", "9144000"))
    cy = int(sld_sz.attrib.get("cy", "6858000"))
    width_pt = cx / EMU_PER_POINT
    height_pt = cy / EMU_PER_POINT
    return width_pt, height_pt


def _normalize_pdf(pdf_path: str, slide_width_pt: float, slide_height_pt: float) -> None:
    reader = PdfReader(pdf_path)
    writer = PdfWriter()

    target_width = max(1.0, slide_width_pt)
    target_height = max(1.0, slide_height_pt)

    for page in reader.pages:
        # reset rotation
        try:
            page.rotation = 0
        except Exception:
            pass

        media = page.mediabox
        page_width = float(media.width)
        page_height = float(media.height)

        margin_x = max(0.0, (page_width - target_width) / 2.0)
        margin_y = max(0.0, (page_height - target_height) / 2.0)

        llx = margin_x
        lly = margin_y
        urx = page_width - margin_x
        ury = page_height - margin_y

        page.mediabox.lower_left = (llx, lly)
        page.mediabox.upper_right = (urx, ury)
        if hasattr(page, "cropbox"):
            page.cropbox.lower_left = (llx, lly)
            page.cropbox.upper_right = (urx, ury)
        if hasattr(page, "artbox"):
            page.artbox.lower_left = (llx, lly)
            page.artbox.upper_right = (urx, ury)

        writer.add_page(page)

    tmp_pdf = pdf_path + ".tmp"
    with open(tmp_pdf, "wb") as out_f:
        writer.write(out_f)
    os.replace(tmp_pdf, pdf_path)
