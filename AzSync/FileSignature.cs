using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Threading.Tasks;

using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;

using Serilog;
using SerilogTimings;
using Octodiff.Core;
namespace AzSync
{
    [Serializable]
    public class FileSignature : ILogging
    {
        #region Constructors
        public FileSignature(FileInfo file)
        {
            File = file;
        }
        #endregion

        #region Properties
        public bool Build { get; protected set; }
        protected Logger<TransferEngine> L = new Logger<TransferEngine>();
        #endregion

        #region Methods
        public byte[] Compute()
        {
            using (Operation op = L.Begin("Build file signature for file {file}", File.FullName))
            {
                using (FileStream basisStream = File.Open(FileMode.Open, FileAccess.Read, FileShare.Read))
                using (MemoryStream ms = new MemoryStream())
                {
                    OctoSigWriter octoSigWriter = new OctoSigWriter(ms);
                    SignatureBuilder signatureBuilder = new SignatureBuilder();
                    signatureBuilder.ProgressReporter = new OctoSigProgressReporter(File);
                    signatureBuilder.Build(basisStream, octoSigWriter);
                    op.Complete();
                    return OctoSig = ms.ToArray();
                }

            }
        }
        #endregion
        
        #region Fields
        [NonSerialized]
        public FileInfo File;
        public string UserName;
        public string ComputerName;
        public string ETag;
        public long Size;
        public byte[] OctoSig;
        #endregion
    }
}
