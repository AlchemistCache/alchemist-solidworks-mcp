"""Background jobs and batches: job_result, execute_batch (plumbing in jobs.py).
Registered by registry.register_all(); call plumbing in bridge.py."""
import asyncio
import concurrent.futures
import json
import time
from typing import Literal


import execution_client
import session
import tool_groups
from bridge import dump, fail, with_image
from jobs import background, jobs, parse_result, resolve, running, SYNC_WAIT, in_batch


def register(mcp, tool):
    # ------------------------------------------------------------------ background jobs
    @tool
    def job_result(job_id: str, wait_s: float = 40.0):
        """Result of a long operation that answered {"running": true, "job_id": …}. Waits up to wait_s (≤45)
        seconds; returns the result (with job_seconds), or the running answer again (then call job_result again)."""
        job = jobs.get(job_id)
        if job is None:
            raise fail("JOB_NOT_FOUND", f"{job_id} (already collected, or the adapter was restarted)")
        tool_name, fut, started = job.label, job.fut, job.started
        try:
            res = fut.result(timeout=max(1.0, min(wait_s, SYNC_WAIT)))
        except concurrent.futures.TimeoutError:
            # a job past a minute may be waiting on a SolidWorks dialog nobody can answer: name it, not after 10 min
            note = execution_client.busy_note(dialogs_only=True) if time.time() - started > 60 else ""
            return running(job_id, tool_name, started, note)
        except Exception:
            jobs.pop(job_id, None)
            raise
        jobs.pop(job_id, None)
        if isinstance(res, str):
            body = parse_result(res)
            body = {"result": res} if "text" in body and len(body) == 1 else body
            return with_image(dump({**body, "job_seconds": round(time.time() - started)}))
        return res

    @tool
    def execute_batch(steps: list[dict] = [], steps_file: str = "", stop_on_error: bool = True,
                      report: Literal["auto", "full", "summary"] = "auto"):
        """Run up to 100 tool calls in ONE request, in order: steps = [{"tool": "<name>", "args": {...}}, …] with
        exactly the arguments of the single tools — or steps_file = a JSON file on disk with that list (a long batch
        then never passes through the conversation). Reuse earlier results in string args: "$last.<path>" or
        "$<n>.<path>" (n = 0-based step) — a path into the step's answer, e.g. "$last.created.0" (the feature it
        made), "$1.study", "$0.components.0.name". report: full = {ok, steps, results: [{step, tool, …its answer |
        error}]}; summary = {ok, steps, failed: [{step, tool, error}], created, removed, saved, last}; auto = summary
        for a steps_file or more than 20 steps. Pictures of view_image / view_images steps come with the answer (up
        to 8). A tool of a group that is off runs here too: its group is turned on (`groups_turned_on`).
        Stops at the first error unless stop_on_error=False. If SolidWorks dies mid-batch it is restarted and
        the batch stops: `solidworks_restarted` says so — unsaved changes of the open documents are lost, `saved`
        lists what reached the disk."""
        if steps_file:
            path = session.full_path("steps_file", steps_file)
            try:
                with open(path, encoding="utf-8-sig") as fh:
                    loaded = json.load(fh)
            except (OSError, ValueError) as ex:
                raise fail("BAD_PARAMETER", f"steps_file {steps_file}: {ex}") from ex
            loaded = loaded.get("steps") if isinstance(loaded, dict) else loaded
            if steps or not isinstance(loaded, list):
                raise fail("BAD_PARAMETER", "give steps OR steps_file (a JSON list of {tool, args}, or {\"steps\": [...]})")
            steps = loaded
        if not steps or len(steps) > 100:
            raise fail("BATCH_SIZE", "1..100 steps")
        # a tool of a group that is off counts: no client has to re-read its tool list to run it
        known = ({t.name for t in mcp._tool_manager.list_tools()} | set(tool_groups.PARKED)) - {"execute_batch",
                                                                                                "job_result"}
        bad = [f"{i}: '{st.get('tool')}'" for i, st in enumerate(steps)
               if not isinstance(st, dict) or st.get("tool") not in known]
        if bad:   # checked BEFORE anything runs — not halfway through with earlier steps already done
            raise fail("BATCH_STEP", f"not a batchable tool: {', '.join(bad)}")
        turned_on = tool_groups.turn_on_for(mcp, [st["tool"] for st in steps])
        brief = report == "summary" or (report == "auto" and (bool(steps_file) or len(steps) > 20))

        def run():
            token = in_batch.set(True)
            results, report_rows, images = [], [], []
            restarted = []
            failed = set()   # step numbers that raised — an answer may carry its own "error" key (a mate's state)

            async def go():
                for i, st in enumerate(steps):
                    name = st.get("tool")
                    try:
                        args = resolve(st.get("args") or {}, results)
                        res = await mcp.call_tool(name, args)   # a failed step raises (ToolError)
                        text = next((c.text for c in res.content if getattr(c, "type", "") == "text"), "")
                        images.extend(c for c in res.content if getattr(c, "type", "") == "image")
                        results.append(parse_result(text))
                        report_rows.append({"step": i, "tool": name, **results[-1]})
                    except Exception as ex:   # noqa: BLE001 — report per step
                        failed.add(i)
                        results.append({"error": str(ex)})
                        report_rows.append({"step": i, "tool": name, "error": str(ex)})
                        if _solidworks_died(str(ex)):
                            restarted.append(await _restart_solidworks())
                            if restarted[-1]:
                                return
                        if stop_on_error:
                            return
            try:
                asyncio.run(go())
            finally:
                in_batch.reset(token)
            ok = len(results) - len(failed)
            out = {"ok": ok, "steps": len(steps)}
            if turned_on:
                out["groups_turned_on"] = turned_on
            if brief:
                out["failed"] = [{k: r[k] for k in ("step", "tool", "error")} for r in report_rows if r["step"] in failed]
                for key in ("created", "removed"):
                    items = [x for r in report_rows for x in (r.get(key) or [])]
                    if items:
                        out[key] = items
                out["saved"] = [r.get("document") or r.get("features") for r in report_rows
                                if r["tool"] == "save_document" and r["step"] not in failed]
                if report_rows and report_rows[-1]["step"] not in failed:
                    out["last"] = {k: v for k, v in report_rows[-1].items() if k not in ("step",)}
            else:
                out["results"] = report_rows
            if len(results) < len(steps):
                out["stopped"] = f"at step {len(results) - 1} ({len(steps) - len(results)} not run)"
            if any(restarted):
                out["solidworks_restarted"] = (f"SolidWorks stopped answering at step {len(results) - 1} and was "
                                               "restarted: documents that were open are closed, their unsaved changes "
                                               "are lost — reopen them and continue from that step")
            return [dump(out)] + images[:8] if images else dump(out)

        async def _restart_solidworks() -> bool:
            """ensure_ready after an RPC failure: True when SolidWorks had to be launched again (it had died)."""
            try:
                res = await mcp.call_tool("ensure_ready", {})
                text = next((c.text for c in res.content if getattr(c, "type", "") == "text"), "")
                return bool(parse_result(text).get("sw_launched"))
            except Exception:   # noqa: BLE001 — the batch report already carries the step's own error
                return False

        return background("execute_batch", run)


_RPC_DEAD = ("0x800706BA", "0x80010108", "RPC_S_SERVER_UNAVAILABLE", "RPC_E_DISCONNECTED", "RPC server is unavailable",
             "сервер RPC недоступен", "отключен от клиентов")


def _solidworks_died(error: str) -> bool:
    """An error that means the SolidWorks process may be gone (not a tool's own refusal)."""
    return any(sig.lower() in error.lower() for sig in _RPC_DEAD)
