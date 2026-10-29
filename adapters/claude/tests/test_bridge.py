"""bridge.py / jobs.py plumbing: $refs in execute_batch, result parsing, BUSY while a background job runs, job TTL."""
import concurrent.futures
import os
import sys
import time

_ADAPTER_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ADAPTER_DIR not in sys.path:
    sys.path.insert(0, _ADAPTER_DIR)

import bridge  # noqa: E402
import jobs  # noqa: E402


def test_parse_and_resolve():
    r = jobs.parse_result('{"state_version":3,"component":"pin-6","origin_mm":[1,2,3]}')
    assert r["component"] == "pin-6"
    r2 = jobs.parse_result('{"state_version":4,"created":["Boss1"],"volume_mm3":5.0}')
    assert jobs.resolve({"c": ["$0.component", "$last.volume_mm3", "$1.created.0"]}, [r, r2]) == {
        "c": ["pin-6", 5.0, "Boss1"]}
    assert jobs.parse_result("not an answer") == {"text": "not an answer"}


def test_one_answer_form():
    """Every server answer becomes ONE object: state_version, document, sketch, the payload flattened in, key=value
    lines as fields, plain names as features, created / removed — no escaped JSON inside JSON."""
    import json
    import response_mapper
    payload = '{"face_count":1,"faces":[{"i":4,"area_mm2":1500}]}'
    text = response_mapper.map_response({
        "status": "COMPLETED", "stateVersion": 2,
        "cadState": {"activeDocument": "Part1", "activeSketch": None,
                     "features": [payload, "Boss1", "{not json", "fixed=True", "material=AISI 1020",
                                  "transform=[1,0,0]"]},
        "result_geometry": {"volume_mm3": 15000.0}, "created": ["Boss1"]})
    assert '\\"' not in text
    a = json.loads(text)
    assert list(a)[:2] == ["state_version", "document"] and "sketch" not in a
    assert a["faces"][0]["i"] == 4 and a["features"] == ["Boss1", "{not json"]
    assert a["fixed"] is True and a["material"] == "AISI 1020" and a["transform"] == [1, 0, 0]
    assert a["volume_mm3"] == 15000.0 and a["created"] == ["Boss1"]
    lst = json.loads(response_mapper.map_response({"status": "COMPLETED", "stateVersion": 5,
                                                   "result_geometry": [1, 2]}))
    assert lst == {"state_version": 5, "result": [1, 2]}
    dup = json.loads(response_mapper.map_response({"status": "DUPLICATE", "last_known_state_version": 7}))
    assert dup["duplicate"] is True and dup["state_version"] == 7


def test_uniform_wraps_every_answer():
    import json
    import response_mapper
    assert json.loads(response_mapper.uniform('{"a":1}', 9)) == {"state_version": 9, "a": 1}
    assert response_mapper.uniform('{"state_version":3,"a":1}', 9) == '{"state_version":3,"a":1}'
    assert json.loads(response_mapper.uniform("[1,2]", 9)) == {"state_version": 9, "items": [1, 2]}
    assert json.loads(response_mapper.uniform("plain words", 9)) == {"state_version": 9, "text": "plain words"}


def test_busy_while_a_job_runs_and_ttl():
    jobs.jobs.clear()
    running = concurrent.futures.Future()
    jobs.jobs["abc"] = job = jobs.Job("import_step_folder", running, time.time() - 5)
    msg = jobs.busy_message()
    assert msg.startswith("BUSY |") and "abc" in msg
    token = jobs.job_active.set(True)      # the job's own calls pass
    assert jobs.busy_message() is None
    jobs.job_active.reset(token)
    job.free = True                        # e.g. CalculiX solving: SolidWorks is free meanwhile
    assert jobs.busy_message() is None
    job.free = False
    running.set_result("done")
    job.started, job.finished = time.time() - 2 * jobs.JOB_TTL, time.time() - 10
    assert jobs.busy_message() is None and "abc" in jobs.jobs         # a long job, finished just now: kept
    job.finished = time.time() - jobs.JOB_TTL - 1
    assert jobs.busy_message() is None and "abc" not in jobs.jobs     # an hour after it finished: dropped


def test_a_job_is_busy_from_its_first_second(monkeypatch):
    """A parallel call answers BUSY while a job runs — from the start, not only once SYNC_WAIT has passed; a call
    answered in time leaves nothing behind."""
    import threading
    jobs.jobs.clear()
    seen, gate = {}, threading.Event()

    def slow():
        seen["msg"] = None
        gate.wait(2)
        return "done"

    def look():
        time.sleep(0.05)
        seen["msg"] = jobs.busy_message()   # another call, outside the job
        gate.set()
    threading.Thread(target=look).start()
    assert jobs.background("sim_crosscheck", slow) == "done"
    assert seen["msg"] and seen["msg"].startswith("BUSY |") and not jobs.jobs


def test_solidworks_free_marks_only_its_own_job():
    jobs.jobs.clear()
    job = jobs.Job("sim_crosscheck", concurrent.futures.Future(), time.time())
    token = jobs.current_job.set(job)
    with jobs.solidworks_free():
        assert job.free
    assert not job.free
    jobs.current_job.reset(token)
    with jobs.solidworks_free():           # outside a job: nothing to mark
        pass


def test_a_long_batch_is_not_busy_for_its_own_steps(monkeypatch):
    """execute_batch past SYNC_WAIT: its steps run on anyio worker threads (as MCP runs sync tools) — they must not
    see the batch's own job as 'SolidWorks busy'."""
    import anyio

    jobs.jobs.clear()
    monkeypatch.setattr(jobs, "SYNC_WAIT", 0.05)

    def batch():
        time.sleep(0.2)          # the call has already returned RUNNING and the job is registered

        async def step():
            return await anyio.to_thread.run_sync(jobs.busy_message)
        return anyio.run(step)

    assert '"running":true' in jobs.background("execute_batch", batch)
    job, = jobs.jobs.values()
    assert job.fut.result(timeout=5) is None
    jobs.jobs.clear()


def test_batch_refs_to_steps_that_have_not_run():
    try:
        jobs.resolve({"x": "$3.study"}, [{"study": "a"}])
    except bridge.CadOperationError as ex:
        assert str(ex).startswith("BATCH_REF | $3.study: step 3 has not run")
    else:
        raise AssertionError("must raise")
