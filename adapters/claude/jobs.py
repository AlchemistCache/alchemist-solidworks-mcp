"""Background jobs and batches. MCP clients cut a tool call at ~60 s while whole-assembly work takes minutes: a call
not done after SYNC_WAIT seconds keeps running on the job pool and answers {"running": true, "job_id": …} (job_result
collects it); while a job owns SolidWorks every other call answers BUSY at once — except while the job is in a phase
that does not use SolidWorks (solidworks_free: CalculiX solving). execute_batch's $refs are resolved here too."""
import concurrent.futures
import contextlib
import contextvars
import json
import re
import threading
import time
import uuid

import config
import execution_client
from adapter_log import write as _log
from response_mapper import dump, fail


SYNC_WAIT = config.SYNC_WAIT
_pool = concurrent.futures.ThreadPoolExecutor(max_workers=4, thread_name_prefix="swmcp-job")
JOB_TTL = 3600.0                      # an uncollected job is dropped an hour after it FINISHED


class Job:
    """One call on the job pool: registered when it starts (so a parallel call answers BUSY from the first second, not
    only after SYNC_WAIT); `free` while it is in a phase that does not use SolidWorks."""

    def __init__(self, label: str, fut, started: float):
        self.label, self.fut, self.started = label, fut, started
        self.finished = None
        self.free = False


jobs = {}   # job_id -> Job
_register = threading.Lock()   # the BUSY check and a new job's registration are one step: two calls cannot both pass
# True inside a background job and in everything it calls — a ContextVar, not threading.local: execute_batch steps run
# on anyio worker threads, which inherit the context but not thread-locals (a batch used to answer BUSY to itself).
job_active = contextvars.ContextVar("swmcp_job_active", default=False)
in_batch = contextvars.ContextVar("swmcp_in_batch", default=False)   # calls inside execute_batch run synchronously
current_job = contextvars.ContextVar("swmcp_current_job", default=None)   # the Job a job's own code runs in


def running(job_id: str, tool: str, started: float, solidworks: str = "") -> str:
    """The answer while a job runs; solidworks = what SolidWorks is waiting on (execution_client.busy_note)."""
    out = {"running": True, "job_id": job_id, "tool": tool, "seconds": round(time.time() - started),
           "next": f"job_result(job_id='{job_id}') for the result — do NOT repeat the original call"}
    if solidworks:
        out["solidworks"] = solidworks
    return dump(out)


def busy_message():
    """While a background job still works in SolidWorks, a new call would only queue behind it on the single STA
    thread (and time out in the client) — answer BUSY at once instead. Calls made BY the job pass."""
    if job_active.get() or in_batch.get():
        return None
    now = time.time()
    for jid, job in list(jobs.items()):
        if job.fut is not None and job.fut.done():   # None: registered, not yet submitted — it is starting
            if job.finished is not None and now - job.finished > JOB_TTL:
                jobs.pop(jid, None)
            continue
        if job.free:          # e.g. CalculiX is solving — SolidWorks itself is idle
            continue
        return (f"BUSY | SolidWorks is still running {job.label} (job_id={jid}, {now - job.started:.0f} s). "
                f"Wait with job_result(job_id='{jid}') and call again after it finished.")
    return None


@contextlib.contextmanager
def solidworks_free():
    """Wrap a phase of a background job that does not touch SolidWorks (CalculiX solving): other calls may use
    SolidWorks meanwhile instead of answering BUSY. Only for a phase after which the job makes no SolidWorks call
    whose result depends on the active document — another call may have changed it meanwhile."""
    # inside execute_batch the batch's later steps use SolidWorks and the active document: the batch stays busy
    job = None if in_batch.get() else current_job.get()
    if job is not None:
        job.free = True
    try:
        yield
    finally:
        if job is not None:
            job.free = False


execution_client.BUSY_CHECK = busy_message


def background(label: str, fn):
    """Run fn() on the job pool — its result if it finishes within SYNC_WAIT, else a running handle for job_result."""
    if in_batch.get():
        return fn()
    job_id, holder = uuid.uuid4().hex[:8], []

    def work():
        token, cur = job_active.set(True), current_job.set(holder[0])
        try:
            return fn()
        finally:
            job_active.reset(token)
            current_job.reset(cur)
    started = time.time()
    with _register:
        # a call that becomes a job checks BUSY here: inside the job job_active is set and the check would pass it
        busy = busy_message()
        if busy:
            _log(f"-> {label} refused: {busy}")
            raise execution_client.ExecutionLayerError(busy)
        holder.append(Job(label, None, started))
        jobs[job_id] = holder[0]
    holder[0].fut = fut = _pool.submit(work)
    try:
        result = fut.result(timeout=SYNC_WAIT)
    except concurrent.futures.TimeoutError:
        _log(f"job {job_id}: {label} still running after {SYNC_WAIT:.0f} s -> RUNNING")

        def finished(f):
            holder[0].finished = time.time()
            ex = f.exception()
            _log(f"job {job_id}: {label} {'FAILED ' + type(ex).__name__ + ': ' + str(ex) if ex else 'done'} "
                 f"after {time.time() - started:.0f} s")
        fut.add_done_callback(finished)
        return running(job_id, label, started)
    except BaseException:
        jobs.pop(job_id, None)
        raise
    jobs.pop(job_id, None)    # answered in time: nothing for job_result to collect
    return result


# ---------------------------------------------------------------- execute_batch $refs
_REF = re.compile(r"^\$(last|\d+)\.(.+)$")


def parse_result(text: str) -> dict:
    """A tool's answer as an object for $refs — every answer is one JSON object; anything else stays as `text`."""
    try:
        body = json.loads(text)
    except ValueError:
        return {"text": text}
    return body if isinstance(body, dict) else {"items": body}


def resolve(value, results):
    if isinstance(value, str):
        m = _REF.match(value)
        if not m:
            return value
        if not results:
            raise fail("BATCH_REF", f"{value}: no earlier step")
        try:
            node = results[-1] if m.group(1) == "last" else results[int(m.group(1))]
        except IndexError:
            raise fail("BATCH_REF", f"{value}: step {m.group(1)} has not run (only {len(results)} so far)")
        for part in m.group(2).split("."):
            try:
                node = node[int(part)] if isinstance(node, list) else node[part]
            except (KeyError, IndexError, ValueError, TypeError):
                raise fail("BATCH_REF", f"{value}: '{part}' not found")
        return node
    if isinstance(value, list):
        return [resolve(v, results) for v in value]
    if isinstance(value, dict):
        return {k: resolve(v, results) for k, v in value.items()}
    return value
