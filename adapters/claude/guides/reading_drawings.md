Reading a drawing (DXF/DWG) and building the part from it

What each field of analyze_drawing's answer MEANS is in its own description; these are the DECISIONS — what to do
with the numbers, and which mistakes are silent. Build with the ordinary tools: create_sketch →
add_sketch_entities → define_sketch (design dimensions, fully defined) → extrude_feature / sheet_metal_feature …

R1 — The numbers are TRUE mm already. Never scale a second time. The reader applied DIMLFAC per view. Where a view
carries its own scale_factor (a detail or section at another ratio), say which factor produced a number.

R2 — The edge CLASS is data, never appearance. A feature drawn VISIBLE is on the face that view shows; drawn
HIDDEN it is on the far side. This one test places a pocket on the correct face (R6).
R2b — A loop is a fact; an open chain is a question: a bend line, a centre line or a silhouette fragment the
chainer stopped at a T-junction. Never close a loop yourself (R17).
R2c — An ellipse measures the third dimension: a circle on a plane tilted by θ projects with ry/rx = cos θ and rx
its true radius. Cross-check the tilt against an angular dimension. A short fitted run pins its parameters badly
even at a good residual. Never round an ellipse arc to a circular arc — sample it as a spline or declare a gap.

R3 — The TITLE BLOCK is a parameter table: a label and its value share an x, one row apart (length, width,
thickness, material, standard, description, scale, projection, weight). A value identical across different parts
is template boilerplate; do not trust a label over geometry (an "A3" printed on an A4 sheet); a constraint in a
note overrides a naive read of the geometry. Read sheet.not_read before concluding anything is absent — a named
block you cannot account for is evidence that did not reach you; say so.

R4 — Alignment gives the axis; the PROJECTION STANDARD gives the sign (config `projection`):
- first_angle (ISO/European, the default): the view BELOW the front view is the TOP view, and the object's BACK is
  the edge adjacent to the front view.
- third_angle (ASME/American): the mirror of that.
A wrong sign builds valid geometry with no error. State the front/back (and left/right) assignment before
placing any depth feature.

R5 — The base profile comes from ONE view; the other views only place things along the third axis. Transcribe the
outline from the loop's own `seq`, in the order given (chamfers and corner radii as sketch entities), extrude it
by the depth an adjacent view gives, then position the rest. Use the loop's `area` for R13.

R6 — Which FACE a pocket sits on is decided by R2, not by plausibility: a channel on the opposite face gave the
same topology, volume and area — only the CG and a face normal differed.
R6b — Bind the drawing's front to the BUILD DIRECTION before the first extrusion. create_sketch reports the
sketch frame (origin, xdir, ydir); extrude_feature grows along the sketch normal unless reverse. Say in one
sentence which face of the part your sketch plane is, and measure every depth from it. A base flange
(sheet_metal_feature) thickens to the sketch plane's −normal side unless reverse_thickness — the opposite habit.
Then read one face or edge back (analyze_model(faces|edges, near=…)) and check the coordinate against the
dimension that fixed it.

R7 — A section view is read along its cut line's axis; decide which side is the front by a feature already
placed (R2/R6). Two views must agree: 40 from the front in the section is 20 from the back in the top view of a
60 mm part.
R8 — An undimensioned twin is symmetric; a feature with no position across an axis is centred on it. Say which.
R9 — A Ø equal to another Ø + 2R is that round's RIM: a concentric Ø15 and Ø19 with R2 arcs into the bore is one
Ø15 hole with an R2 fillet at its mouth.

Sheet metal
R10 — The reader pairs bend notes with bend lines; read the result. `in_loop: true` on a bend line is a warning
(the match landed on outline geometry); `unpaired` means the reader refused to guess — resolve it from the drawing
or declare it a gap.
R11 — Build sheet metal as FLAT BLANK + sketched bends: sheet_metal_feature base_flange (thickness from the
thickness view or a bare "2 mm" note; bend radius and K from the notes), then a sketched bend per direction group
(create_sketch on the flat face → add_sketch_entity(line) → sheet_metal_feature sketched_bend). Fold the outer
bends first when an inner region must stay flat for a later sketch; the fixed point must sit on material that
stays put for every bend.
R12 — Bent-state dimensions convert to the flat: segment = outer_dim − Σ OSSB + Σ BA/2 over its bends, with
BA = θ·(R + K·t) and OSSB = (R + t)·tan(θ/2); K = 0.5 by default. If a flat-pattern view is also there, measure it
and cross-check; a mismatch means the K assumption is wrong — flag it.

Self-verification — there is NO original part
R13 — Compute the expected volume from the drawing BEFORE building (profile area × depth − pockets − holes −
corner treatments; sheet metal: blank area × thickness), then read mass_properties after and compare.
R14 — Volume, area and topology cannot see a MIRROR. For every feature placed from a SECOND view, read the built
geometry back (analyze_model(edges|faces, near=…)) and compare that coordinate with the dimension. A skipped bend
does not change the volume either — only your own ledger shows it.
R15 — Close the ledger: every dimension and primitive ends consumed by a feature, a duplicate/silhouette of one,
or a written gap. Say what you did NOT build and why.
R16 — Never compare two analyses by their version label: compare source.sha256 first (different files?), then the
field set, only then the values. A direct .DXF and a .DWG→DXF conversion are not the same sheet.
R17 — An unresolved contour is a GAP, never a guess: a wrong contour builds valid geometry silently. A Tier B
boundary (planar face traversal) is a reading, but check its area/bbox against a stated size before building.

Real production drawings
R18 — printed_mismatch: on a linear dimension the scale or the match is wrong; on an angle the value is a
judgement (no reflex angles on a mechanical drawing — take the explement). kind_from: "printed" means the drawn
symbol overruled the entity type (a 60 across a circle is a Ø60 hole, not a length). Beside a mismatch, distrust
the `measures` link, not the value.
R19 — A BREAK view gives the PROFILE, never the LENGTH: collinear silhouette pairs with a gap, break lines
overhanging both sides, in matched pairs. The length comes from a dimension or the title block; a dimension on the
broken geometry may be the drawn extent — let R20 arbitrate.
R20 — If the title block states a WEIGHT, it must come out: volume × density, before building. It caught a break
view's true length (338 mm gives 0.464 kg, the drawn 125 gives 0.172).
R21 — Sheet metal by DECLARATION first (Blech, plate, лист …), else a single-extrude part ≤ 20 mm thick is sheet
metal too. Say which way you decided.
R22 — With no UP/DOWN note, the bend direction comes from the projection (R4) on the formed view, not from the
line's class. Build once, read the fold back (R14), flip if mirrored.
R23 — On sheet metal, BENDS come before chamfers and fillets: a sketched bend across a chamfered edge returns
null with no useful message.
