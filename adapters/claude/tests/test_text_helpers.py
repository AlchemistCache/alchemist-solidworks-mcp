"""Small text rules the model reads: where tool_docs._cut shortens a description, and the error text of a FAILED
execution response (response_mapper.failure_of), also when the server sent no message."""
import os
import sys

_ADAPTER_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ADAPTER_DIR not in sys.path:
    sys.path.insert(0, _ADAPTER_DIR)

import response_mapper as rm  # noqa: E402
import tool_docs  # noqa: E402


def test_cut_at_a_sentence_end_or_a_line_break():
    text = "This first sentence is long enough to count. More text that runs past the limit"
    assert tool_docs._cut(text, 60) == "This first sentence is long enough to count."
    assert tool_docs._cut("First line without a stop\nsecond line that is long enough to pass the limit", 60) == \
        "First line without a stop"
    assert tool_docs._cut(text, len(text)) == text


def test_cut_does_not_end_at_eg_ie_or_too_early():
    assert tool_docs._cut("Uses e.g. analyze_model and i.e. the faces list, then more words follow here until "
                          "the limit is hit", 60) == "Uses e.g. analyze_model and i.e. the faces list, then more"
    # a sentence end before 40 % of the limit would drop most of it: the last word boundary instead
    assert tool_docs._cut("Is it? Yes! And then a long tail of words that continues well beyond the limit", 60) == \
        "Is it? Yes! And then a long tail of words that continues"


def test_failure_of_reads_code_and_message():
    ex = rm.failure_of({"status": "FAILED", "error": {"code": "NOT_FOUND", "message": "no such file"}},
                       "opening 'a.SLDPRT'")
    assert isinstance(ex, rm.CadOperationError) and str(ex) == "NOT_FOUND | opening 'a.SLDPRT': no such file"


def test_failure_of_without_a_message():
    assert str(rm.failure_of({"status": "FAILED", "error": {"code": "SAVE_FAILED"}})) == \
        "SAVE_FAILED | the execution server sent no message"
    assert str(rm.failure_of({"status": "FAILED", "error": {"code": "X", "message": ""}}, "saving")) == \
        "X | saving: the execution server sent no message"
    assert str(rm.failure_of({"status": "FAILED"})) == "FAILED | the execution server sent no message"
