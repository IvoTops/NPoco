using System;
using System.Collections.Generic;
using System.Text;

namespace NPoco
{
    public class InsertBulkOptions
    {
        public int? BulkCopyBatchSize { get; set; }
        public int? BulkCopyTimeout { get; set; }
        public bool BulkCopyStreaming { get; set; }
        public bool BulkCopyUseInternalTransaction { get; set; }
    }
}
