using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    // ===================== BOM table on a drawing =====================
    // insert_bom_table — BOM of the assembly shown in a drawing view (IView.InsertBomTable4), anchored at a sheet corner
    //                    or placed at x/y; optional auto-balloons (AutoBalloon5) numbered from the BOM.
    public partial class SolidWorksService
    {
        public ExecutionResponse InsertBomTable(ToolRequest request) => Run(request, true, p =>
        {
            var dr = ActiveDrawingOrFail(out var d, hint: "create_drawing + add_drawing_view of an assembly");
            string vn = PStr(p, "view");
            var view = FindDrawingView(dr, vn, v => (v.ReferencedDocument as IModelDoc2)?.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
                       ?? throw new ToolFail("VIEW_NOT_FOUND", vn != null ? $"No view '{vn}'. Views: {string.Join(", ", DrawingViewNames(dr))}" : "No view of an assembly on the active sheet.");
            string tpl = PStr(p, "template") ?? LangFile("bom-standard.sldbomtbt") ?? throw new ToolFail("TEMPLATE_NOT_FOUND", "bom-standard.sldbomtbt not found; pass template.");
            int bt;
            switch (PStr(p, "bom_type", "top_level"))
            {
                case "top_level": bt = (int)swBomType_e.swBomType_TopLevelOnly; break;
                case "parts_only": bt = (int)swBomType_e.swBomType_PartsOnly; break;
                case "indented": bt = (int)swBomType_e.swBomType_Indented; break;
                default: throw new ToolFail("BAD_PARAMETER", "bom_type must be top_level, parts_only or indented.");
            }
            int anchor;
            switch (PStr(p, "anchor", "top_right"))
            {
                case "top_left": anchor = (int)swBOMConfigurationAnchorType_e.swBOMConfigurationAnchor_TopLeft; break;
                case "bottom_left": anchor = (int)swBOMConfigurationAnchorType_e.swBOMConfigurationAnchor_BottomLeft; break;
                case "bottom_right": anchor = (int)swBOMConfigurationAnchorType_e.swBOMConfigurationAnchor_BottomRight; break;
                default: anchor = (int)swBOMConfigurationAnchorType_e.swBOMConfigurationAnchor_TopRight; break;
            }
            bool at = p["x_mm"] == null || p["y_mm"] == null;
            string cfg = PStr(p, "config") ?? view.ReferencedConfiguration ?? "";
            var t = view.InsertBomTable4(at, PNum(p, "x_mm", 0) / MM, PNum(p, "y_mm", 0) / MM, anchor, bt, cfg, tpl, false,
                bt == (int)swBomType_e.swBomType_Indented ? (int)swNumberingType_e.swNumberingType_Detailed : (int)swNumberingType_e.swNumberingType_None,
                false) as IBomTableAnnotation;
            if (t == null) throw new ToolFail("BOM_FAILED", $"InsertBomTable4 returned null (template {Path.GetFileName(tpl)}, view {view.Name}).");
            var ta = t as ITableAnnotation;
            var res = new JObject { ["view"] = view.Name, ["template"] = tpl, ["bom_type"] = PStr(p, "bom_type", "top_level"), ["config"] = cfg };
            try
            {
                res["rows"] = ta.RowCount; res["columns"] = ta.ColumnCount;
                // cell text carries format tags ("<FONT size=13PTS>ПОЗИЦИЯ") — strip them
                Func<int, int, string> cell = (r, c) => System.Text.RegularExpressions.Regex.Replace(ta.get_Text(r, c) ?? "", "<[^>]*>", "").Trim();
                res["header"] = new JArray(Enumerable.Range(0, ta.ColumnCount).Select(c => cell(0, c)));
                res["first_rows"] = new JArray(Enumerable.Range(1, Math.Min(5, ta.RowCount - 1))
                    .Select(r => new JArray(Enumerable.Range(0, ta.ColumnCount).Select(c => cell(r, c)))));
            }
            catch (Exception ex) { res["read_note"] = ex.Message; }
            if (PBool(p, "balloons", false))
            {
                d.ClearSelection2(true);
                d.Extension.SelectByID2(view.Name, "DRAWINGVIEW", 0, 0, 0, false, 0, null, 0);
                var o = dr.CreateAutoBalloonOptions() as AutoBalloonOptions;
                o.Layout = (int)swBalloonLayoutType_e.swDetailingBalloonLayout_Square;
                o.IgnoreMultiple = true;
                var balloons = dr.AutoBalloon5(o) as object[];
                d.ClearSelection2(true);
                res["balloons"] = balloons?.Length ?? 0;
            }
            d.EditRebuild3();
            return res;
        });
    }
}
