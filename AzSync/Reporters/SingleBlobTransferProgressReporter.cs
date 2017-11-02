//---------------------------------------------------------------------------------------------------------------------------------------
// Uses code from Samples.cs from Azure Storage Data Movement Library for .Net: https://github.com/Azure/azure-storage-net-data-movement 
//    Copyright (c) Microsoft Corporation
//---------------------------------------------------------------------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.DataMovement;

namespace AzSync
{
    public class SingleBlobTransferProgressReporter : ILogging, IProgress<TransferStatus>
    {
        #region Constructors
        public SingleBlobTransferProgressReporter(ICloudBlob blob) : base()
        {
            Blob = blob;
            markPosition = Blob.Properties.Length / 10;
            lastTime = DateTime.Now;
        }
        #endregion

        #region Methods
        public void Report(TransferStatus progress)
        {
            this.latestBytesTransferred = progress.BytesTransferred;
            this.latestNumberOfFilesTransferred = progress.NumberOfFilesTransferred;
            this.latestNumberOfFilesSkipped = progress.NumberOfFilesSkipped;
            this.latestNumberOfFilesFailed = progress.NumberOfFilesFailed;
            TimeSpan elapsed = DateTime.Now - lastTime;
            if (latestBytesTransferred == 0 && latestNumberOfFilesFailed == 0 && latestNumberOfFilesSkipped == 0 && latestNumberOfFilesTransferred == 0)
            {
                return;
            }
            else if ((latestBytesTransferred >= mark * markPosition) && (latestBytesTransferred >= (mark + 1) * markPosition))
            {
                ++mark;
                Tuple<double, string> b = TransferEngine.PrintBytesToTuple(latestBytesTransferred / elapsed.TotalSeconds, "/s");
                L.Info("Transferred {0} bytes for 1 file. Transfer rate: {1} {2}", latestBytesTransferred, b.Item1, b.Item2);
            }
            else if (latestNumberOfFilesTransferred == 1)
            {
                L.Info("{0} file transfer(s) completed, {1} skipped, {2} failed.", latestNumberOfFilesTransferred, latestNumberOfFilesSkipped, latestNumberOfFilesFailed);
            }
        }
        #endregion

        #region Fields
        protected Logger<TransferEngine> L = new Logger<TransferEngine>();
        protected ICloudBlob Blob;
        private long latestBytesTransferred;
        private long latestNumberOfFilesTransferred;
        private long latestNumberOfFilesSkipped;
        private long latestNumberOfFilesFailed;
        protected CancellationToken CT;
        int mark = 0;
        long markPosition;
        DateTime lastTime;
        #endregion
    }
}
