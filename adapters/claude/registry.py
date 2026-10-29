"""Registers every tool family onto server.mcp — one module per subject, each with register(mcp, tool); the call
plumbing is bridge.py (calls) and jobs.py (background jobs, BUSY, batches).

Units, one rule for the model: millimetres and degrees everywhere (kg, N, MPa); the older tools whose server side
is SI convert at the boundary in units.py. Each tool belongs to a group (by module,
tool_groups.py); the groups that are off are parked after registration."""
import tool_groups
import tools_analysis
import tools_assembly
import tools_core
import tools_document
import tools_drawing
import tools_features
import tools_images
import tools_sheet_metal
import tools_simulation
import tools_sketch
import tools_weldments

MODULES = (tools_document, tools_sketch, tools_features, tools_drawing, tools_analysis,
           tools_core, tools_assembly, tools_sheet_metal, tools_simulation, tools_weldments, tools_images)


def register_all(mcp):
    tool = mcp.tool(structured_output=False)   # every tool returns text (+ an image for the picture tools)
    for module in MODULES:
        before = {t.name for t in mcp._tool_manager.list_tools()}
        module.register(mcp, tool)
        tool_groups.assign({t.name for t in mcp._tool_manager.list_tools()} - before, module.__name__)
