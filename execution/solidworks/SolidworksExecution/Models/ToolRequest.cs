namespace SolidworksExecution.Models
{
    public class ToolRequest
    {
        public string OperationId { get; set; }
        public string Tool { get; set; }
        public int StateVersion { get; set; }
        public object Params { get; set; }
        // When the client stops waiting (its HTTP timeout). A request still queued behind a long job on the single
        // STA thread is NOT executed after it — the caller already reported a timeout and would not see the result.
        public System.DateTime? DeadlineUtc { get; set; }
    }
}
