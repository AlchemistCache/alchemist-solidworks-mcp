"""Documents and files: start SolidWorks, new / open / activate / close / save, export, state and selection,
material, document units, STEP / IGES import (one multibody part), batch export of files, VBA macros.
Units: MILLIMETRES and DEGREES (converted to SI for the server in units.py).
Registered by registry.register_all(); call plumbing in bridge.py."""
import os
from typing import Literal, Optional


import session
from bridge import call, dump, call_mm, call_mm_json
from jobs import background
from execution_client import ensure_ready as _ensure_ready, ExecutionLayerError
from response_mapper import CadOperationError, execution_layer_error, fail


def register(mcp, tool):
    # ---------------------------------------------------------------------------
    # Tool: ensure_ready  (lifecycle / bootstrap — not a state-versioned CAD op)
    # ---------------------------------------------------------------------------
    @tool
    def ensure_ready(close_dialogs: bool = False) -> str:
        """Bring the SolidWorks environment up and confirm it is ready to use.

        Starts the execution server if it isn't running, and launches SolidWorks if it is closed
        (just attaches if it's already open). Does NOT open or create any document — use
        open_new_part / create_drawing for that (so assembly/drawing workflows aren't forced into a
        part). Safe to call anytime; idempotent. Call this first at the start of a session, or
        whenever a tool fails with a connection / COM-attach error.
        close_dialogs=True: SolidWorks waits on a dialog nobody can answer in its hidden instance (BUSY or a timeout
        names it) — the server closes it (Cancel / No first) and the call it held ends."""
        try:
            body = _ensure_ready(close_dialogs)
        except ExecutionLayerError as ex:
            raise execution_layer_error(ex) from ex
        if not body.get("comAttached"):
            raise fail("NOT_READY", f"SolidWorks is not ready: {body.get('launchError') or body.get('ensureError')}")
        session.set_state_version(body.get("stateVersion"))
        out = {"state_version": body.get("stateVersion"), "ready": True, "document": body.get("activeDocument"),
               "sw_version": body.get("swVersion"), "sw_launched": body.get("swLaunched")}
        if body.get("dialogsClosed"):
            out["dialogs_closed"] = body["dialogsClosed"]
        return dump(out)

    # ---------------------------------------------------------------------------
    # Tool: open_new_part
    # ---------------------------------------------------------------------------
    @tool
    def open_new_part(template_path: str = "") -> str:
        """Open a new SolidWorks part document."""
        return call("open_new_part", {"template_path": template_path})

    # ---------------------------------------------------------------------------
    # Tool: open_document
    # ---------------------------------------------------------------------------
    @tool
    def open_document(file_path: str, as_assembly: bool = False) -> str:
        """Open an EXISTING document from disk (counterpart to open_new_part, which makes a BLANK doc).
        file_path: full path to the file.
          - Native .sldprt / .sldasm / .slddrw open directly.
          - Foreign .step / .iges / .x_t / NX .prt / .ipt / .CATPart import via a STAGED path: the
            CLASSIC translator (LoadFile4) first — proven for STEP/IGES and NX .prt — with a
            3D-Interconnect retry as the last resort. A clear OPEN_FAILED means no usable translator:
            convert to STEP or defer that file.
          - as_assembly=True (foreign files only): the file is an ASSEMBLY STEP — the classic import
            would flatten it to a MULTIBODY PART; this asks for a real assembly import instead.
        Makes the opened document active and bumps state_version."""
        return call("open_document", {"file_path": file_path, "as_assembly": as_assembly})

    # ---------------------------------------------------------------------------
    # Tool: export_document
    # ---------------------------------------------------------------------------
    @tool
    def export_document(file_path: str, formats: list[Literal["STEP", "IGES", "STL", "PDF", "DWG", "DXF"]] = []) -> str:
        """Export the ACTIVE document (it stays open). formats empty = from file_path's extension (.step/.stp, .igs,
        .stl, .pdf, .dwg, .dxf); one format = file_path; several = one file each, file_path with the extension
        replaced (partial failures listed). DWG/DXF need a drawing (create_drawing; a sheet-metal flat pattern is
        export_flat_dxf). IGES is written as .igs. Many files from disk: batch_export_files."""
        # export does NOT change CAD state; the execution layer returns the same state_version.
        return call("export_document", {"file_path": file_path, "formats": formats})

    # ---------------------------------------------------------------------------
    # Tool: verify_state
    # ---------------------------------------------------------------------------
    @tool
    def verify_state() -> str:
        """Read and return the current CAD state without modifying it."""
        return call("verify_state", {})

    # ---------------------------------------------------------------------------
    # Tool: close_document
    # ---------------------------------------------------------------------------
    @tool
    def close_document(save: bool = False, close_all: bool = False, titles: list[str] = [], hidden_only: bool = False,
                       include_modified: bool = False) -> str:
        """Close the active document (save=True saves it first; an error if that fails — it stays open).
        close_all=True closes ALL documents (incl. invisibly-loaded component docs), DISCARDING unsaved changes — a
        full reset between assembly jobs. titles = close just those; hidden_only=True = every invisible background
        doc; in both a modified doc is skipped unless include_modified (its changes are discarded)."""
        if titles or hidden_only:
            return call_mm("close_document", dict(titles=titles, hidden_only=hidden_only or None,
                         include_modified=include_modified or None))
        return call("close_document", {"save": save, "close_all": close_all or None})

    # ---------------------------------------------------------------------------
    # Tool: save_document
    # ---------------------------------------------------------------------------
    @tool
    def save_document(file_path: str = "", save_all: bool = False, copy_only: bool = False,
                      repoint_assemblies: bool = False) -> str:
        """Save the active SolidWorks document to disk. file_path: full output path with the document extension
        (.sldprt / .sldasm / .slddrw) — Save As; omitted = save in place (the document must have been saved before).
        save_all=True also saves every modified document it references (subassemblies, parts) — a top-level save
        alone leaves them modified. Save As of a part / subassembly that open assemblies use would make them ALL use
        the new file: refused unless copy_only=True (write a copy, change nothing else) or repoint_assemblies=True.
        The answer lists still_modified documents (not saved yet). A part must be saved before a drawing view can
        reference it."""
        return call("save_document", {"file_path": file_path, "save_all": save_all or None, "copy_only": copy_only or None,
                                      "repoint_assemblies": repoint_assemblies or None})

    # ---------------------------------------------------------------------------
    # Tool: get_selection
    # ---------------------------------------------------------------------------
    @tool
    def get_selection() -> str:
        """Report what the USER currently has selected in the SolidWorks GUI — the inverse of index-based selection.
        When the user clicks geometry in the SolidWorks window (e.g. while telling you what to do — "put a hole on
        THIS face"), call this to learn exactly which entity they mean. Each selected item is mapped to the SAME
        stable index analyze_model(faces/edges) reports, so you can act on it immediately:
          • a FACE  → {type:'face', i, planar, normal?, area, point?} → create_sketch(on_face=True, face_index=i)
          • an EDGE → {type:'edge', i, start, end, mid}              → add_edge_feature(edge_indices=[i])
          • a VERTEX → {type:'vertex', point:[x,y,z]};  a reference PLANE → {type:'plane', name}
        Returns {selected_count, selection:[...]}. i = -1 if a selected face/edge couldn't be matched to a
        solid-body index. Read-only: does NOT change state_version and does NOT clear the selection — but call it
        BEFORE any tool that clears the selection (create_sketch, add_edge_feature, extrude cut, etc.), otherwise
        the user's pick is gone. If selected_count is 0, ask the user to click the face/edge they mean."""
        return call("get_selection", {})

    # ---------------------------------------------------------------------------
    # Tool: set_part_material
    # ---------------------------------------------------------------------------
    @tool
    def set_part_material(
        material_name: str,
        library: str = "SolidWorks Materials",
    ) -> str:
        """Assign a material to the active part document. Applied to all configurations.
        material_name: exact library name (e.g. '1060 Alloy', 'AISI 1020', 'ABS') — must match the SOLIDWORKS Materials database exactly (e.g. '1060 Alloy', NOT 'Aluminum 1060 Alloy').
        library: material library name (default 'SolidWorks Materials'). list_materials(query) finds the exact
        names; a name not found answers MATERIAL_NOT_FOUND with the closest ones.
        The applied material is visible in Mass Properties and the feature tree.
        Requires an active part document — not a drawing."""
        return call("set_part_material", {"material_name": material_name, "library": library})

    # ---------------------------------------------------------------------------
    # Tool: activate_document
    # ---------------------------------------------------------------------------
    @tool
    def activate_document(title: str) -> str:
        """Switch the ACTIVE SolidWorks document to an already-open one by its title (e.g. 'gear' or
        'gear.SLDPRT'). Use this to read/compare another open part — e.g. activate the original, analyze_model
        it, then activate your copy — without OS window switching. Does NOT change geometry or state_version.
        The document must already be OPEN. Returns the now-active document title."""
        return call("activate_document", {"title": title})

    # ------------------------------------------------------------------ files: units, STEP import, batch export, macros (mm)
    @tool
    def batch_export_files(files: list[str], format: str = "step", out_dir: str = "") -> str:
        """Export many FILES (parts/assemblies/drawings by path) to a format (step, iges, stl, x_t, pdf, dxf…):
        each is opened read-only, saved as <out_dir or its folder>\\<name>.<format>, closed. Errors per file."""
        return call_mm("batch_export_files", dict(files=files, format=format, out_dir=out_dir))

    @tool
    def unreferenced_files(folder: str, roots: list[str], exclude: list[str] = []) -> str:
        """Model files (.sldprt / .sldasm) under `folder` that none of the `roots` (top assemblies, full paths) uses —
        read from the files' references on disk, nothing is opened. exclude = path fragments to skip (e.g. a library
        folder). Also drawings_of_unreferenced: drawings whose models are all unused. Paths relative to folder."""
        return call_mm("unreferenced_files", dict(folder=folder, roots=roots, exclude=exclude))

    @tool
    def run_macro(macro: str, module: str = "", procedure: str = "main", args: dict = {}) -> str:
        """Run a SolidWorks VBA macro (.swp / .swb) from the project's macros folder ONLY
        (SWMCP_MACROS_DIR, default <repo>\\macros). module default '<file name>1'. args = {"name": value, …} →
        written to Environ("TEMP") & "\\SolidWorksMCP\\macro_args.txt" as "name=value" lines (CRLF, system code page —
        read them with Line Input and Split(line, "=")). A VBA error box is closed by the server and its text comes
        back in the error (MACRO_FAILED) or in dialogs_closed; VBA_BLOCKED = the VBA engine is stuck (ensure_ready)."""
        return call_mm("run_macro", dict(macro=macro, module=module, procedure=procedure, args=args))

    @tool
    def import_step(file_path: str, out_path: str = "", overwrite: bool = False, inspect_only: bool = False) -> str:
        """A manufacturer's STEP / IGES (often a whole assembly) → ONE multibody .SLDPRT (default: next to the file,
        same name) — not an assembly with a file per component. Native bodies (3D Interconnect off), the imported
        document is closed without saving, the translator's .err log goes into the answer. Reports the bodies of the
        STEP vs the part (warning if fewer), surface bodies = pieces the translator could not knit into solids.
        inspect_only=True only reports what the file contains."""
        return call_mm("import_step", dict(file_path=file_path, out_path=out_path, overwrite=overwrite or None,
                     inspect_only=inspect_only or None))

    @tool
    def import_step_folder(folder: str, recursive: bool = True, overwrite: bool = False,
                           extensions: list[str] = [".step", ".stp"], max_files: int = 500) -> str:
        """import_step for every STEP in a folder (sub-folders too): a .SLDPRT next to each file, existing ones are
        skipped unless overwrite. Long — answers {"running": true, "job_id": …}; job_result gives per-file results
        and totals."""
        exts = tuple(e.lower() for e in extensions)
        folder = session.full_path("folder", folder)
        if not os.path.isdir(folder):   # before the job: os.walk would answer "0 files", os.listdir crash in the job
            raise fail("FOLDER_NOT_FOUND", f"no folder '{folder}'")

        def run():
            files = []
            for base, _, names in os.walk(folder) if recursive else [(folder, None, os.listdir(folder))]:
                files += [os.path.join(base, n) for n in names if n.lower().endswith(exts)]
            found, files = len(files), sorted(files)[:max_files]
            rows, done, skipped, failed, warned = [], 0, 0, 0, 0
            for f in files:
                try:
                    r = call_mm_json("import_step", dict(file_path=f, overwrite=overwrite or None))
                    if r.get("skipped"):
                        skipped += 1
                    else:
                        done += 1
                        warned += 1 if (r.get("warning") or r.get("surface_bodies")) else 0
                    rows.append({k: r.get(k) for k in ("file", "part", "skipped", "bodies", "solid_bodies", "surface_bodies",
                                                        "warning", "seconds") if r.get(k) is not None})
                except CadOperationError as ex:
                    failed += 1
                    rows.append({"file": f, "error": str(ex)})
            return dump({"folder": folder, "files": len(files), "converted": done, "skipped": skipped, "failed": failed,
                          "with_surface_bodies_or_warnings": warned, "results": rows,
                          "not_processed": found - len(files) or None})   # max_files cut the list: said, not silent
        return background("import_step_folder", run)

    @tool
    def set_document_units(length: Literal["mm", "cm", "m", "in"] = "mm", decimals: Optional[int] = None,
                           angle: Optional[Literal["deg", "rad"]] = None) -> str:
        """Units of the ACTIVE document (custom unit system): length unit, decimal places, angle unit."""
        return call_mm("set_document_units", dict(length=length, decimals=decimals, angle=angle))
