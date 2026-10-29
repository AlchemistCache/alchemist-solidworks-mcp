using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using SolidworksExecution.Infrastructure;
using SolidworksExecution.Models;
using SolidworksExecution.Services;

namespace SolidworksExecution.Controllers
{
    [RoutePrefix("api/tool")]
    public class ToolController : ApiController
    {
        private readonly SolidWorksService _service;
        private readonly IOperationGuard _guard;

        public ToolController() : this(OperationGuard.Instance) { }

        public ToolController(IOperationGuard guard)
        {
            _guard = guard;
            _service = new SolidWorksService(guard);
        }

        // Read-only resync endpoint: returns the current authoritative state_version
        // WITHOUT a state_version check or increment. The adapter calls this to recover
        // from a desync (e.g. the execution server was restarted after a rebuild).
        [HttpGet]
        [Route("state")]
        public HttpResponseMessage GetState()
        {
            return Request.CreateResponse(HttpStatusCode.OK, new
            {
                stateVersion = _guard.GetCurrentStateVersion()
            });
        }

        // Health endpoint. `~/` escapes the api/tool prefix → GET /health. Reports the server is
        // up, the current state_version, and whether SolidWorks COM is currently attachable (probed on the
        // STA thread — COM_ATTACH is lazy/per-request, so this is a fresh attach attempt) plus the active
        // document. Never 500s: a COM probe failure is reported as comAttached=false, not an error status.
        // While a call holds the STA thread, or our own SolidWorks shows a dialog (a COM probe could wait on it), it
        // answers at once without the probe: busy (the tool) + busySeconds, and the dialogs — one nobody answers holds
        // SolidWorks for good; POST /close_dialogs closes them.
        [HttpGet]
        [Route("~/health")]
        public HttpResponseMessage Health()
        {
            var info = new Dictionary<string, object>
            {
                ["status"] = "UP",
                ["stateVersion"] = _guard.GetCurrentStateVersion(),
                ["serverTimeUtc"] = DateTime.UtcNow.ToString("o"),
            };
            var work = StaExecutor.Instance.Current;
            var dialogs = SolidWorksService.OwnDialogs();
            if (work != null || dialogs.Count > 0)
            {
                if (work != null)
                {
                    info["busy"] = work.Label;
                    info["busySeconds"] = Math.Round((DateTime.UtcNow - work.SinceUtc).TotalSeconds);
                }
                info["dialogs"] = dialogs;
                ExecLog.Write($"<- health UP busy={work?.Label ?? "-"} dialogs={dialogs.Count}");
                return Request.CreateResponse(HttpStatusCode.OK, info);
            }
            try
            {
                var com = StaExecutor.Instance.Run(() => _service.GetHealthInfo(), "health");
                foreach (var kv in com) info[kv.Key] = kv.Value;
            }
            catch (Exception ex)
            {
                info["comAttached"] = false;
                info["comError"] = ex.Message;
            }
            object attached;
            info.TryGetValue("comAttached", out attached);
            ExecLog.Write($"<- health UP comAttached={attached}");
            return Request.CreateResponse(HttpStatusCode.OK, info);
        }

        // Lifecycle bootstrap (ensure_ready). `~/` escapes the api/tool prefix → POST /ensure_ready.
        // POST because it is side-effecting: it launches SolidWorks via COM if it isn't running.
        // Distinct from /health (a read-only probe) — this one makes the environment ready. Does NOT
        // open/create a document. Runs on the STA thread (touches COM) and never 500s (a launch
        // failure is reported as comAttached=false). Not part of the state_version/idempotency
        // envelope — it's a lifecycle call, not a CAD operation.
        [HttpPost]
        [Route("~/ensure_ready")]
        public HttpResponseMessage EnsureReady()
        {
            var info = new Dictionary<string, object>
            {
                ["status"] = "UP",
                ["stateVersion"] = _guard.GetCurrentStateVersion(),
                ["serverTimeUtc"] = DateTime.UtcNow.ToString("o"),
            };
            try
            {
                var com = StaExecutor.Instance.Run(() => _service.EnsureReady(), "ensure_ready");
                foreach (var kv in com) info[kv.Key] = kv.Value;
            }
            catch (Exception ex)
            {
                info["comAttached"] = false;
                info["ensureError"] = ex.Message;
            }
            object attached, launched;
            info.TryGetValue("comAttached", out attached);
            info.TryGetValue("swLaunched", out launched);
            ExecLog.Write($"<- ensure_ready UP comAttached={attached} swLaunched={launched}");
            return Request.CreateResponse(HttpStatusCode.OK, info);
        }

        // POST /close_dialogs — closes the dialogs our own hidden SolidWorks shows (Cancel / No first): the way out when a
        // call waits on one nobody can answer (/health lists them while busy). Off the STA thread and without COM — the
        // thread is the one waiting. Nothing in the user's SolidWorks (own-instance mode only).
        [HttpPost]
        [Route("~/close_dialogs")]
        public HttpResponseMessage CloseDialogs()
        {
            var closed = SolidWorksService.CloseOwnDialogs();
            ExecLog.Write($"<- close_dialogs closed={closed.Count}");
            return Request.CreateResponse(HttpStatusCode.OK, new Dictionary<string, object>
            {
                ["closed"] = closed,
                ["ownInstance"] = SolidWorksService.OwnMode,
            });
        }

        [HttpPost]
        [Route("execute")]
        public HttpResponseMessage Execute([FromBody] ToolRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.OperationId) || string.IsNullOrEmpty(request.Tool))
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "operation_id and tool are required." });

            ExecLog.Write($"-> {request.Tool} op={request.OperationId} sv={request.StateVersion}");

            ExecutionResponse response;

            try
            {
                // All COM-touching work runs on the single dedicated STA thread — OWIN request
                // threads are MTA, which made sheet-metal calls return null and add_edge_feature deadlock.
                response = StaExecutor.Instance.Run(() =>
                {
                    if (request.DeadlineUtc.HasValue && System.DateTime.UtcNow > request.DeadlineUtc.Value.ToUniversalTime())
                        return new ExecutionResponse
                        {
                            OperationId = request.OperationId, Status = "FAILED", StateVersion = OperationGuard.Instance.GetCurrentStateVersion(),
                            Error = new ExecutionError { Code = "EXPIRED", Message = "The client stopped waiting before SolidWorks got to this request (it queued behind a long operation); not executed." }
                        };
                    return Dispatch(request);
                }, request.Tool);
            }
            catch (System.Exception ex)
            {
                ExecLog.Write($"<- {request.Tool} UNHANDLED {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = ex.Message });
            }

            if (response == null)
            {
                ExecLog.Write($"<- {request.Tool} UNKNOWN_TOOL");
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = $"Unknown tool: {request.Tool}" });
            }

            if (response.Status == "FAILED")
                ExecLog.Write($"<- {request.Tool} FAILED {response.Error?.Code}: {response.Error?.Message}");
            else
                ExecLog.Write($"<- {request.Tool} {response.Status} sv={response.StateVersion}");

            return Request.CreateResponse(HttpStatusCode.OK, response);
        }

        // Routes a tool request to its service method. Runs on the STA thread (see Execute).
        // Returns null for an unknown tool so the caller can emit the BadRequest response.
        private ExecutionResponse Dispatch(ToolRequest request)
        {
            switch (request.Tool)
            {
                // --- documents, sketches, features, drawings, analysis (lengths in meters) ---
                case "open_new_part":          return _service.OpenNewPart(request);
                case "open_document":          return _service.OpenDocument(request);
                case "create_sketch":          return _service.CreateSketch(request);
                case "add_sketch_entity":      return _service.AddSketchEntity(request);
                case "add_sketch_entities":    return _service.AddSketchEntities(request);
                case "add_dimension":          return _service.AddDimension(request);
                case "define_sketch":          return _service.DefineSketch(request);
                case "add_sketch_constraint":  return _service.AddSketchConstraint(request);
                case "add_edge_feature":       return _service.AddEdgeFeature(request);
                case "create_rib":             return _service.CreateRib(request);
                case "create_drawing":         return _service.CreateDrawing(request);
                case "add_drawing_view":       return _service.AddDrawingView(request);
                case "add_flat_pattern_view":  return _service.AddFlatPatternView(request);
                case "add_drawing_dimension":  return _service.AddDrawingDimension(request);
                case "auto_dimension_drawing": return _service.AutoDimensionDrawing(request);
                case "auto_center_marks":      return _service.AutoCenterMarks(request);
                case "add_hole_callout":       return _service.AddHoleCallout(request);
                case "add_section_view":       return _service.AddSectionView(request);
                case "save_document":          return _service.SaveDocument(request);
                case "export_document":        return _service.ExportDocument(request);
                case "extrude_feature":        return _service.ExtrudeFeature(request);
                case "verify_state":           return _service.VerifyState(request);
                case "close_document":         return _service.CloseDocument(request);
                case "analyze_model":          return _service.AnalyzeModel(request);
                case "check_drawing":    return _service.CheckDrawing(request);
                case "get_selection":          return _service.GetSelection(request);
                case "edit_sketch":            return _service.EditSketch(request);
                case "add_reference_geometry": return _service.AddReferenceGeometry(request);
                case "create_pattern":         return _service.CreatePattern(request);
                case "set_part_material":      return _service.SetPartMaterial(request);
                case "sheet_metal_feature":    return _service.SheetMetalFeature(request);
                case "modify_dimension":       return _service.ModifyDimension(request);
                case "edit_feature":           return _service.EditFeature(request);
                case "activate_document":      return _service.ActivateDocument(request);
                case "analyze_assembly":       return _service.AnalyzeAssembly(request);
                case "open_new_assembly":      return _service.OpenNewAssembly(request);
                case "insert_component":       return _service.InsertComponent(request);
                case "add_mate":               return _service.AddMate(request);
                case "save_body_as_part":      return _service.SaveBodyAsPart(request);
                // --- assemblies, sheet metal, simulation, weldments, images, STEP import (mm / degrees / kg) ---
                case "model_snapshot":         return _service.ModelSnapshot(request);
                case "list_components":        return _service.ListComponents(request);
                case "list_open_documents":    return _service.ListOpenDocuments(request);
                case "interference_check":     return _service.InterferenceCheck(request);
                case "clearance_check":        return _service.ClearanceCheck(request);
                case "measure_components":     return _service.MeasureComponents(request);
                case "mass_properties":        return _service.MassProperties(request);
                case "list_references":        return _service.ListReferences(request);
                case "get_custom_properties":  return _service.GetCustomProperties(request);
                case "set_custom_properties":  return _service.SetCustomProperties(request);
                case "configurations":         return _service.Configurations(request);
                case "bom_export":             return _service.BomExport(request);
                case "set_component_state":    return _service.SetComponentState(request);
                case "set_appearance":         return _service.SetAppearance(request);
                case "move_component":         return _service.MoveComponent(request);
                case "replace_component":      return _service.ReplaceComponent(request);
                case "pack_and_go":            return _service.PackAndGo(request);
                case "export_components":      return _service.ExportComponents(request);
                case "delete_components":      return _service.DeleteComponents(request);
                case "sketch_modify":          return _service.SketchModify(request);
                case "list_materials":         return _service.ListMaterials(request);
                case "flat_geometry":          return _service.FlatGeometry(request);   // internal: sheet_metal_dfm
                case "unreferenced_files":     return _service.UnreferencedFiles(request);
                case "form_subassembly":       return _service.FormSubassembly(request);
                case "sheet_metal_info":       return _service.SheetMetalInfo(request);
                case "export_flat_dxf":        return _service.ExportFlatDxf(request);
                case "view_image":             return _service.ViewImage(request);
                case "add_plane_angle":        return _service.AddPlaneAngle(request);
                case "add_axis_two_points":    return _service.AddAxisTwoPoints(request);
                case "sweep_pipe":             return _service.SweepPipe(request);
                case "loft":                   return _service.Loft(request);
                case "set_body_visibility":    return _service.SetBodyVisibility(request);
                case "batch_export_files":     return _service.BatchExportFiles(request);
                case "run_macro":              return _service.RunMacro(request);
                case "list_mates":             return _service.ListMates(request);
                case "edit_mate":              return _service.EditMate(request);
                case "add_mate_advanced":      return _service.AddMateAdvanced(request);
                case "insert_and_mate":        return _service.InsertAndMate(request);
                case "pattern_components":     return _service.PatternComponents(request);
                case "constraint_status":      return _service.ConstraintStatus(request);
                case "sheet_metal_cutlist":    return _service.SheetMetalCutlist(request);
                case "sheet_metal_hem":        return _service.SheetMetalHem(request);
                case "sheet_metal_miter_flange": return _service.SheetMetalMiterFlange(request);
                case "sheet_metal_cross_break": return _service.SheetMetalCrossBreak(request);
                case "convert_to_sheet_metal": return _service.ConvertToSheetMetal(request);
                case "hole_wizard":            return _service.HoleWizard(request);
                case "import_step":            return _service.ImportStep(request);
                case "add_sketch_text":        return _service.AddSketchText(request);
                case "set_document_units":     return _service.SetDocumentUnits(request);
                case "move_drawing_view":      return _service.MoveDrawingView(request);
                case "layout_drawing_views":   return _service.LayoutDrawingViews(request);
                case "dedupe_drawing_dimensions": return _service.DedupeDrawingDimensions(request);
                case "add_dimension_between":  return _service.AddDimensionBetween(request);
                case "insert_bom_table":       return _service.InsertBomTable(request);
                case "weldment_profiles":      return _service.WeldmentProfiles(request);
                case "create_weldment_profiles": return _service.CreateWeldmentProfiles(request);
                case "weldment_member":        return _service.WeldmentMember(request);
                case "weldment_cut_list":      return _service.WeldmentCutList(request);
                case "weldment_trim":          return _service.WeldmentTrim(request);
                case "weldment_end_cap":       return _service.WeldmentEndCap(request);
                case "weldment_gusset":        return _service.WeldmentGusset(request);
                case "sheet_metal_gusset":     return _service.SheetMetalGusset(request);
                // --- Simulation (SolidWorksService.Simulation.cs; N, MPa, mm) ---
                case "sim_status":             return _service.SimStatus(request);
                case "sim_create_study":       return _service.SimCreateStudy(request);
                case "sim_delete_study":       return _service.SimDeleteStudy(request);
                case "sim_set_material":       return _service.SimSetMaterial(request);
                case "sim_add_fixture":        return _service.SimAddFixture(request);
                case "sim_add_load":           return _service.SimAddLoad(request);
                case "sim_contact":            return _service.SimContact(request);
                case "sim_mesh":               return _service.SimMesh(request);
                case "sim_run":                return _service.SimRun(request);
                case "sim_results":            return _service.SimResults(request);
                case "sim_plot":               return _service.SimPlot(request);
                case "sim_image":              return _service.SimImage(request);
                case "sim_connector":          return _service.SimConnector(request);
                case "sim_contact_set":        return _service.SimContactSet(request);
                case "sim_mesh_control":       return _service.SimMeshControl(request);
                case "sim_options":            return _service.SimOptions(request);
                case "sim_copy_study":         return _service.SimCopyStudy(request);
                case "sim_report":             return _service.SimReport(request);
                case "sim_export_mesh":        return _service.SimExportMesh(request);
                default:                       return null;
            }
        }
    }
}
