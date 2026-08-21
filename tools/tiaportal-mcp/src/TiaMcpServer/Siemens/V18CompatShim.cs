#if TIA_V18
using System.Collections.Generic;

namespace Siemens.Engineering
{
    public enum ImportDocumentOptions
    {
        None = 0,
        Override = 1,
        SkipInactiveCultures = 2,
        ActivateInactiveCultures = 4
    }

    public enum DocumentResultState
    {
        Success = 0,
        Warning = 1,
        Error = 2
    }

    public enum TransferResultState
    {
        Success = 0,
        Warning = 1,
        Error = 2,
        PartialSuccess = 3
    }

    public class DocumentImportResult
    {
        public DocumentResultState State { get; set; }
        public IEnumerable<object> ImportedPlcBlocks { get; } = new List<object>();
    }

    public class DocumentExportResult
    {
        public DocumentResultState State { get; set; }
    }

    public class TransferResult
    {
        public TransferResultState State { get; set; }
        public IEnumerable<HW.TransferResultMessage> Messages { get; } = new List<HW.TransferResultMessage>();
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
    }
}

namespace Siemens.Engineering.HW
{
    public class TransferResultMessage
    {
        public string Message { get; set; } = "";
        public string State { get; set; } = "";
        public IEnumerable<TransferResultMessage> Messages { get; } = new List<TransferResultMessage>();
    }
}
#endif
