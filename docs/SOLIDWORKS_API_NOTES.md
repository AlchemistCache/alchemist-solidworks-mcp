# SolidWorks 2024 API notes

What we learned the hard way on a live SOLIDWORKS 2024 SP1 (English and Russian UI), driving it through COM from a
hidden instance. Most of it is not in the API help. Each rule is what the code in this repository relies on.

## Speed

- **A COM call into a hidden SolidWorks costs 5–15 ms** (even with `CommandInProgress`): 138 edges × ~10 calls = 6 s.
  Read in bulk: `IPartDoc.GetTessTriangles` (the whole part in one call), `ISketch.GetLines2` (12 numbers per line,
  the ends in 6..11), `IFace2.Normal` (one call; 0 for a curved face). Transform points with plain arithmetic on
  `ArrayData`, not through `MathPoint`.
- A helper thread that runs beside every call (the lone-OK watcher) must wait on an event the call's end sets, not
  `Thread.Sleep(1000)`: joining a sleeping thread rounded every call up to the next whole second (a 0.34 s
  `view_image` answered in 1.02 s).

## Documents and the "modified" flag

- **Reads set the modified flag too:** `UpdateCutList`, `ExportToDWG2`, switching a configuration there and back.
  The API cannot clear it. A component part without its own window is reset by `OpenDoc6` + `CloseDoc` (SolidWorks
  re-reads it and marks nothing else); `ReloadOrReplace` of a component marks the top assembly (part in a
  subassembly) and the parts derived from it (a mirrored one); reloading an assembly reloads its components — the
  next call through an old subassembly pointer crashed SolidWorks (0x800706BE).
- In a hidden SolidWorks `IModelDoc2.Visible` is false for every document — "invisible" is not "background";
  a background document is one without a window (`GetFirstModelView() == null`).
- A never-saved drawing is not closed by `CloseDoc`, neither by its title ("Draw1 - Sheet1") nor by "Draw1";
  `QuitDoc` closes it.
- An import (`LoadFile4`, `OpenDoc6` of a STEP / IGES / foreign file) makes a new document from a template; with
  "prompt user to select document template" (Tools > Options > Default Templates) it shows «New SOLIDWORKS Document»
  and waits — in the hidden instance for good (8 h, the server and `/health` with it). Every import sets "always use
  the default templates" for its duration (`ImportPrefs`); the watch beside every call presses Cancel in the picker
  after 3 s should it still come. A call held by any dialog: `/health` answers `busy` + `dialogs` at once,
  `ensure_ready(close_dialogs=True)` closes them (Cancel / No first).

## Pictures

- With "View PNG / JPEG / TIF After Saving" on (Export Settings in the registry; not in the API) SolidWorks opens
  every saved PNG / JPEG / TIF in the image viewer, and a running SolidWorks does not re-read the flag. `view_image`
  saves in a format whose flag is off (PNG, else TIF, else JPEG).
- `IAssemblyDoc.Isolate` is display only (the document is not marked modified) and a zoom-to-fit after it frames the
  chosen components tightly — but SaveAs to an image still draws the isolated-away components, so such a frame is
  taken with `SaveBMP` (it takes what the window shows). `ViewZoomTo2` fits a 3D box including its depth: flatten the
  box along the line of sight. The frame's background is exactly 255,255,255 and light faces are 255,255,249 —
  crop by white, not by the corner pixel. SaveAs returns a frame of a different size and orientation than the paper
  (asked 960×720, got 450×599), hence paper × 2.6 and re-shooting narrow frames.
- The hidden instance does not render Simulation plots into SaveAs / SaveBMP — `sim_image` draws its own map from the
  solver's node data.
- Perspective (`IModelView.AddPerspective`), shadows in shaded mode, ambient occlusion and full-scene anti-aliasing
  all show in a `SaveBMP` of the hidden window (`view_image(render=True)`); the last three are SYSTEM options
  (`swDisplayShadowsInShadedMode`, `swDisplayAmbientOcclusionShadows`, `swSystemOptionDisplayAntiAliasing`) shared with
  the user's SolidWorks through the registry — set for the shot and put back. What is SELECTED is drawn blue in the
  shot (the last sketch of a freshly built part: its points and edges) — a render shot clears the selection first.

## Sketches

- In an open sketch `SelectByID2(…, "SKETCHPOINT", x, y, z)` takes MODEL coordinates, not the sketch's: a corner at
  sketch (20, 10) on the Top plane is model (20, 0, −10). Pick by distance over `GetSketchPoints2` instead.
- `ISketchRelationManager.AddRelation` crashes inside SW 2024 (RPC_E_SERVERFAULT) — relations only by selection +
  `SketchAddConstraints`. `FullyDefineSketch` without tying the profile to the origin only adds dimensions and the
  profile floats; with a rectangle's construction diagonals it dimensioned their crossing instead of the height —
  so real segments first, then all. A dimension SolidWorks made driven (a repeat) must be deleted, not undone with
  `EditUndo2` — after the undo the next close of the part crashed (3 of 3). Ends drawn at one point (AddToDB) are
  already one point. An arc drawn as two halves cannot be fully defined: the joint slides along the circle.
- `CreateCornerRectangle` (with the rectangle's construction lines on) adds two construction diagonals and a centre
  point where they cross — with no relation: the sketch stays under-defined with no free segment. `define_sketch`
  ties such a point to the middle of a diagonal.
- A pattern's instance count is a dimension too (`swDimensionParamTypeInteger`): its system value is the bare number.
  Treated as a length it reads 42 teeth as 42 m.
- `InsertSketchText`'s last two arguments are percentages (width, spacing): spacing 0 stacks every letter on the
  first, and a longer text gives an outline SolidWorks will not cut. Its point is in SKETCH coordinates.
- `SelectByID2("FACE" / "EDGE", x, y, z)` shoots a ray from the screen: a face seen edge-on (a box's side from the
  front view) or an edge hidden from the view is not found, and a point on no face picks whatever the ray meets (a rib
  12 mm away became a sketch plane). Faces and edges are found by distance (`GetClosestPointOn`).

## Sheet metal

- Flat pattern without a window: `SetSuppression2(unsuppress)` + `EditRebuild3` of the Flat-Pattern feature in a
  component's document works (70–200 ms). The bend-lines sketch of a part without bends is empty and lies in another
  plane; an unfolded sheet may lie tilted (find the plane by clustering distances, not by rounding). SolidWorks' DXF
  of a tilted part is rotated (a 340×460 opening gave a 468×540 box).
- `IPartDoc.ExportToDWG2` (flat DXF) fails while the SolidWorks window is hidden — the window is shown for the export.
- Flat-pattern `UiBend` features have no definition — bend radii are read from the `OneBend` / `SketchBend` sub-features;
  bend lines in SolidWorks' DXF are LINEs of colour 8.
- Sheet-metal gusset: `InsertSheetMetalGussetFeature2` (arrays) and `CreateDefinition(swFmSMGusset)` silently return
  null — selecting faces / edge / vertex + `InsertSheetMetalGussetFeature3` works.
- An edge flange at an angle: the profile starts at the end of the first edge, on a plane perpendicular to it.

## Weldments

- The arrays in `IStructuralMemberGroup.Segments`, `InsertStructuralWeldment4/5`, `InsertWeldmentTrimFeature2`,
  `InsertEndCapFeature2`, `InsertGussetFeature2` must be TYPED (`SketchSegment[]`, `StructuralMemberGroup[]`, `Body2[]`,
  `Face2[]`) — with `object[]` the group is empty and the calls return null. The API does not add the Weldment feature
  by itself (no cut list without it) — `weldment_member` inserts it.

## Assemblies

- `InterferenceDetectionManager.Done()` only after every result has been read.
- A component's `Transform2.ArrayData` holds its axes as ROWS (rows (0,1,0), (-1,0,0), (0,0,1) laid a link along +Y),
  translation in metres at 9..11.
- Setting `Transform2` on 60 components one call each, with a rebuild after every one, took half a minute a pose —
  `move_component(placements=…)` sets them all and rebuilds once. Each `Transform2` set still costs ~45 ms (graphics
  and tree updates off did not help); reading the tree before and after for created / removed items cost another
  3 s on a 64-component arm and is skipped for moves.

## Simulation

- Sheet metal is meshed as shells: put loads on the large faces of a sheet — a load on a thin side face does not reach
  the shell's mid-surface (the result is zeros).
- `ICWPressure.Unit` is not applied — pressure is written in Pa (the tool takes MPa and converts).
- Factor of safety of a shell: `NShellOptions` 1 is the TOP face only; 3 (`swsFOS_ShellFaceOption_MinAmongTopAndBottomFace`)
  takes the worse face — the bottom of a bent sheet carries membrane + bending too.
- After a failed `CreateMesh`, `ICWMesh.ElementCount` can still be the OLD mesh's — check `MeshState`. A mesh out of
  date (`ExistsAndNotCurrent`, e.g. after a mesh control) keeps its size in `ElementSize` (in `ICWMesh.Unit`).
- `DeleteContactSet` / `DeleteMeshControl` return nothing: a wrong name deletes nothing — compare the counts.
- `GetStressForEntities3` crashes SW 2024 — do not use it.
- A directional force needs a reference plane (the Front plane is used: its directions are global X / Y / Z).
- Contact SETS (`CreateContactSet2`) number the types differently from the global contact: no penetration 0, bonded 1,
  shrink fit 2, free 3 (the `swsContactType_e` value gave "shrink fit", error 11).
- `AddEdgeWeldConnector` takes only bare COM objects (with `DispatchWrapper` — WrongEdgeSelection), while
  `AddRestraint` / `AddForce` / `AddBoltConnector` want `DispatchWrapper`.
- A bolt in the loads list has type 36 (not in `swsLoadsAndRestraintsType_e`); `ICWMesh.ElementCount` of a new study
  may show another study's mesh — check `MeshState`; `ICWMesh.ElementSize` is in metres.
- `GenerateReport` automates MS Word, which hung (hidden) after the first report — our own HTML report is more reliable.
- `CreateNewStudy3` refuses a study name with "." or "," with err 2 — the same code as a taken name.
- `SelectByID2("FACE", x, y, z)` shoots a ray from the screen through the point: a face hidden from the view (a hole
  wall) is missed or the face in front of it is picked. `point_mm` finds the face by distance (`IFace2.GetClosestPointOn`).
- The entity of a sheet-metal shell (`ICWShell.GetEntityAt`) is an `IBody2`; its component is on its faces.
  `GetNodeDataFromEntity` gives no nodes for the solid face a shell is fixed by (there are none on it) — the nodes are
  found from the face's tessellation, which is in the COMPONENT's coordinates.
- `GetElementDataFromEntity` of a shell's body returns its elements as `GetElements` records (16 numbers each).
- A sharp edge of a bonded contact (a welded-on nut, a tab in a slot) is a stress singularity, like a rigid fixture:
  check "the max away from fixtures" with a second mesh, and judge welds by hand calculation.
- A part whose study was never saved shows a message box ("storage for study … not found") each time it is
  activated or closed; in the hidden instance it blocks the call (activate 197 s, close 627 s). Every call now presses
  a lone OK left open for 3 s and reports it as `messages_closed`.
- SolidWorks meshes slivers (raised 0.8 mm text) that CalculiX rejects: "nonpositive jacobian" — `sim_crosscheck`
  moves the midside nodes of such C3D10 elements onto their straight edges.

## Drawings

- SolidWorks multiplies ROW vectors by its matrices: the view's Z axis (toward the viewer) in model space is the third
  ROW of `ModelToViewTransform.IInverse().ArrayData` (d[6..8]); the third column equals it only for the front view.
- Insert Model Items (`InsertModelAnnotations3`) inserts nothing, without an error, into a drawing that was never saved.
  It may also put one model dimension into several views even with "eliminate duplicates". From SELECTED features
  (`swImportModelItemsFromSelectedFeature`, the features selected as "Boss-Extrude1@part-1@Drawing View1") it inserted
  nothing on SW 2024 — `auto_dimension_drawing(features=…)` inserts all and deletes the others.
- The dimensions come in where they stood in the sketch — across the views. `AlignDimensions(swAlignDimensionType_
  AutoArrange)` sets the SELECTED ones outside the view (linear stacked, the shortest nearest; radii and diameters on
  leaders). `IAnnotation.Select3` selected nothing there; `SelectByID2(GetNameForSelection(), "DIMENSION")` does.
- A GOST title block takes the developer from `$PRP:"п_Разраб"` of the drawing, which the template links to the model's
  property — set the drawing's own property to change it.
- A view's label ("Isometric", "A–A") follows its view by itself when `IView.Position` or the scale changes (checked
  by DXF export); `IAnnotation.SetPosition2` sends it far away, and its `GetExtent` / `GetText` through the API are
  worthless (stale box, empty text; unlabelled principal views report 7.7 mm wide "labels"). A `SaveBMP` picture shows
  labels late — only a DXF export is proof. The label stands above the view, its top 26–28 mm over the outline.
- `SelectByID2`'s pick tolerance is in PIXELS: on a whole-sheet zoom two edges 1.25 mm apart pick the same edge —
  zoom to the point first (`ViewZoomTo2`).
- Named views are localized: a Russian SolidWorks' `CreateDrawViewFromModelView3` knows "*Спереди", "*Изометрия",
  not "*Front".

## Known limitations

- `configurations add` makes the new configuration active.
- `pack_and_go` closes every document of its instance (so it refuses while there are unsaved ones).
- `move_component`: mates may bring a component back on rebuild.
- `export_flat_dxf`: the hidden SolidWorks window shows for a moment during the export.
- Simulation studies live in the document — without saving the part / assembly they are lost when it closes.
