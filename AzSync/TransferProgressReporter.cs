//---------------------------------------------------------------------------------------------------------------------------------------
// Uses code from Samples.cs from Azure Storage Data Movement Library for .Net: https://github.com/Azure/azure-storage-net-data-movement 
//    Copyright (c) Microsoft Corporation
//---------------------------------------------------------------------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

using Microsoft.WindowsAzure.Storage.DataMovement;

namespace AzSync
{
    public class TransferProgressReporter : ILogging, IProgress<TransferStatus>
    {

        #region Methods
        public void Report(TransferStatus progress)
        {
            this.latestBytesTransferred = progress.BytesTransferred;
            this.latestNumberOfFilesTransferred = progress.NumberOfFilesTransferred;
            this.latestNumberOfFilesSkipped = progress.NumberOfFilesSkipped;
            this.latestNumberOfFilesFailed = progress.NumberOfFilesFailed;
            if (latestBytesTransferred == 0 && latestNumberOfFilesFailed == 0 && latestNumberOfFilesSkipped == 0 && latestNumberOfFilesTransferred == 0)
            {
                return;
            }
            else
            {
                L.Info("Transferred {0} bytes with {1} file transfer(s) completed, {2} skipped, {3} failed.", latestBytesTransferred, latestNumberOfFilesTransferred, latestNumberOfFilesSkipped, latestNumberOfFilesFailed);
            }
        }
        #endregion

        #region Fields
        protected Logger<SyncEngine> L = new Logger<SyncEngine>();
        private long latestBytesTransferred;
        private long latestNumberOfFilesTransferred;
        private long latestNumberOfFilesSkipped;
        private long latestNumberOfFilesFailed;
        protected CancellationToken CT;
        #endregion
    }
}
