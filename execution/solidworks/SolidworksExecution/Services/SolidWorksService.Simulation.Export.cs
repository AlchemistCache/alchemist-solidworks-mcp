using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.cosworks;
using SolidWorks.Interop.sldworks;
using SolidworksExecution.Infrastructure;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    // ===================== Study mesh export for an independent solver =====================
    // sim_export_mesh — nodes, elements and named node sets (from face/edge entity specs) of a meshed study plus the
    //                   material constants and the shells (thickness, elements), written to a JSON file (mm) that
    //                   adapters/claude/calculix.py turns into a CalculiX deck. Idea and evidence approach:
    //                   wzyn20051216/solidworks-automation-skill (MIT).
    public partial class SolidWorksService
    {
        public ExecutionResponse SimExportMesh(ToolRequest request) => Run(request, false, p =>
        {
            var d = ActiveDocOrFail();
            var study = Study(StudyMgr(), PStr(p, "study"));
            var mesh = study.Mesh as ICWMesh ?? throw new ToolFail("NO_MESH", "Study mesh unavailable.");
            int state = -1; try { state = mesh.MeshState; } catch { }
            if (state != (int)swsMeshState_e.swsMeshStateExistsAndCurrent) throw new ToolFail("NO_MESH", "Mesh the study first (sim_mesh).");
            int shells = 0; try { shells = (study.ShellManager as ICWShellManager)?.ShellCount ?? 0; } catch { }
            var nodes = ToDoubles(mesh.GetNodes());
            var elems = ToDoubles(mesh.GetElements());
            int cerr; var conn = ToDoubles(mesh.GetConnectivity(out cerr));
            // material constants of the first body / shell that has a material (SI → MPa)
            var mat = new JObject();
            Action<object> take = m =>
            {
                var cm = m as ICWMaterial; if (cm == null || mat["E_mpa"] != null) return;
                bool td;
                try
                {
                    double ex = cm.GetPropertyByName2((int)swsUnitSystem_e.swsUnitSystemSI, "EX", out td);
                    if (ex <= 0) return;
                    mat["name"] = cm.MaterialName; mat["E_mpa"] = ex / 1e6;
                    mat["nu"] = cm.GetPropertyByName2((int)swsUnitSystem_e.swsUnitSystemSI, "NUXY", out td);
                    double y = cm.GetPropertyByName2((int)swsUnitSystem_e.swsUnitSystemSI, "SIGYLD", out td);
                    if (y > 0) mat["yield_mpa"] = y / 1e6;
                }
                catch { }
            };
            try
            {
                var smgr = study.SolidManager as ICWSolidManager;
                for (int i = 0; smgr != null && i < smgr.ComponentCount; i++)
                {
                    int e1; var comp = smgr.GetComponentAt(i, out e1) as ICWSolidComponent; if (comp == null) continue;
                    for (int j = 0; j < comp.SolidBodyCount; j++) { int e2; take((comp.GetSolidBodyAt(j, out e2) as ICWSolidBody)?.GetSolidBodyMaterial()); }
                }
            }
            catch { }
            // shells (sheet metal is meshed at its mid-surface): the thickness and the elements of each → *SHELL SECTION
            var shellList = new JArray();
            var shellNodes = new Dictionary<string, HashSet<int>>();   // component → its shell's nodes
            var shm = study.ShellManager as ICWShellManager;
            for (int i = 0; shm != null && i < shm.ShellCount; i++)
            {
                int e3; var sh = shm.GetShellAt(i, out e3) as ICWShell; if (sh == null) continue;
                try { take(sh.GetShellMaterial()); } catch { }
                string name = sh.Name, comp = null;
                var ids = new SortedSet<int>();
                var nodeIds = new HashSet<int>();
                for (int k = 0; k < sh.EntityCount; k++)
                {
                    // the shell's entity is the sheet body; its elements come as the GetElements records (16 numbers: id, 10 nodes …)
                    object ent = sh.GetEntityAt(k);
                    if (ent is IBody2 body)
                    {
                        if (string.IsNullOrEmpty(name)) name = body.Name;
                        try { comp = comp ?? ((body.GetFirstFace() as IEntity)?.GetComponent() as IComponent2)?.Name2; } catch { }
                    }
                    double[] ed = null; int n;
                    try { ed = ToDoubles(mesh.GetElementDataFromEntity(ent, out n)); } catch (Exception ex) { ExecLog.Write("sim_export_mesh: shell elements -> " + ex.Message); }
                    for (int j = 0; ed != null && j + 15 < ed.Length; j += 16)
                    {
                        ids.Add((int)ed[j]);
                        for (int c = 1; c <= 10; c++) if (ed[j + c] >= 0) nodeIds.Add((int)ed[j + c]);
                    }
                }
                if (comp != null) shellNodes[comp] = nodeIds;
                shellList.Add(new JObject
                {
                    ["name"] = string.IsNullOrEmpty(name) ? comp ?? "" : name, ["elements"] = new JArray(ids),
                    ["thickness_mm"] = sh.ShellThickness * SimUnitMm(sh.ShellUnit)
                });
            }
            var res = new JObject { ["study"] = study.Name, ["nodes"] = mesh.NodeCount, ["elements"] = mesh.ElementCount, ["shells"] = shells, ["material"] = mat,
                                    ["shell_list"] = new JArray(shellList.Select(x => new JObject { ["name"] = x["name"], ["thickness_mm"] = x["thickness_mm"],
                                                                                                    ["elements"] = ((JArray)x["elements"]).Count })),
                                    ["raw_nodes_len"] = nodes.Length, ["raw_elements_len"] = elems.Length, ["raw_conn_len"] = conn.Length };
            if (PBool(p, "probe", false))
            {
                res["raw_elements_head"] = new JArray(elems.Take(60));
                res["raw_conn_head"] = new JArray(conn.Take(60));
            }
            var sets = new JObject();
            var byGeometry = new JArray();
            double halfT = shellList.Select(x => (double)x["thickness_mm"] / 2).DefaultIfEmpty(0).Max();
            if (p["sets"] is JObject specs)
                foreach (var kv in specs)
                {
                    var ids = new SortedSet<int>();
                    foreach (var ent in SimEntities(d, kv.Value as JArray, kv.Key))
                    {
                        object e = ((DispatchWrapper)ent).WrappedObject;
                        int n, added = 0;
                        double[] data = null;
                        if (!PBool(p, "by_distance", false))   // by_distance: the fallback below only (to check it)
                        {
                            try { data = ToDoubles(mesh.GetNodeDataFromEntity(e, out n)); } catch (Exception ex) { ExecLog.Write("sim_export_mesh: GetNodeDataFromEntity -> " + ex.Message); }
                            if (data == null || data.Length == 0) { try { data = ToDoubles(mesh.GetNodeDataFromEntity(ent, out n)); } catch { } }
                        }
                        if (PBool(p, "probe", false)) res["raw_set_" + kv.Key] = new JArray((data ?? new double[0]).Take(24));
                        // [node id, x, y, z] per node (verified with probe)
                        for (int i = 0; data != null && i + 3 < data.Length; i += 4) { ids.Add((int)data[i]); added++; }
                        // A sheet-metal face of an assembly component gives no shell nodes (seen: the fixed bolt holes of a bracket): take
                        // the nodes within half the thickness of the face (the mid-surface under it, or its edge for a side face)
                        if (added == 0 && halfT > 0 && e is IFace2 face)
                        {
                            // only the face's own sheet: the edge of a gusset welded onto a plate lies ON the plate's face
                            string fc = null;
                            try { fc = ((face as IEntity)?.GetComponent() as IComponent2)?.Name2; } catch { }
                            added = NodesNearFace(face, nodes, halfT, 0.05 * Math.Max(mesh.ElementSize * MM, 0.5),
                                                  fc != null && shellNodes.TryGetValue(fc, out var own) ? own : null, ids);
                            if (added > 0 && !byGeometry.Any(x => (string)x == kv.Key)) byGeometry.Add(kv.Key);
                        }
                    }
                    sets[kv.Key] = new JArray(ids);
                }
            res["set_sizes"] = new JObject(sets.Properties().Select(x => new JProperty(x.Name, ((JArray)x.Value).Count)));
            if (byGeometry.Count > 0) res["sets_by_distance"] = byGeometry;
            string fp = PStr(p, "file_path");
            if (fp != null)
            {
                EnsureFolderFor(fp);
                var o = new JObject { ["units"] = "mm, N, MPa", ["study"] = study.Name, ["document"] = d.GetTitle(),
                                      ["nodes"] = new JArray(Enumerable.Range(0, nodes.Length / 4).Select(i => new JArray((int)nodes[4 * i], nodes[4 * i + 1] * MM, nodes[4 * i + 2] * MM, nodes[4 * i + 3] * MM))),
                                      ["element_stride"] = 16, ["elements_raw"] = new JArray(elems), ["sets"] = sets, ["material"] = mat, ["shells"] = shells,
                                      ["shell_list"] = shellList };
                File.WriteAllText(fp, o.ToString(Newtonsoft.Json.Formatting.None));
                res["file"] = fp;
            }
            return res;
        });

        // Shell nodes ([id, x, y, z] … m; in `only`, if given) for a face: ON it (within tol mm) when the mid-surface
        // crosses it — a sheet's side face, a hole — else within half the thickness + tol (the mid-surface under a
        // sheet's large face). → ids; returns how many new.
        private static int NodesNearFace(IFace2 face, double[] nodes, double halfT, double tol, HashSet<int> only, SortedSet<int> ids)
        {
            var tris = new List<double[][]>();
            FaceTrianglesMm(face, tris);
            if (tris.Count == 0) return 0;
            double reach = halfT + tol;
            double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
            foreach (var t in tris)
                foreach (var v in t)
                    for (int k = 0; k < 3; k++) { lo[k] = Math.Min(lo[k], v[k] - reach); hi[k] = Math.Max(hi[k], v[k] + reach); }
            var near = new List<(int id, double d)>();
            for (int i = 0; i + 3 < nodes.Length; i += 4)
            {
                var q = new[] { nodes[i + 1] * MM, nodes[i + 2] * MM, nodes[i + 3] * MM };
                if (only != null && !only.Contains((int)nodes[i])) continue;
                if (q[0] < lo[0] || q[0] > hi[0] || q[1] < lo[1] || q[1] > hi[1] || q[2] < lo[2] || q[2] > hi[2]) continue;
                double dist = BoundaryDistance(q, new List<double[]>(), tris, 0);
                if (dist <= reach) near.Add(((int)nodes[i], dist));
            }
            double cut = near.Any(x => x.d <= tol) ? tol : reach;
            return near.Count(x => x.d <= cut && ids.Add(x.id));
        }
    }
}
