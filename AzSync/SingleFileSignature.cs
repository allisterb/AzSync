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
    public class SingleFileSignature : ILogging
    {
        #region Constructors
        public SingleFileSignature(FileInfo file, CloudBlob blob)
        {
            File = file;
            FileBlob = blob;
            FileName = File.Name;
            FilePath = File.FullName;
            Size = File.Length;
            ETag = blob.Properties.ETag;
        }
        #endregion

        #region Properties
        public bool Build { get; protected set; }
        #endregion

        #region Methods
        public byte[] Compute()
        {
            using (Operation op = L.Begin("Build file signatures for file {file}", File.FullName))
            {
                using (FileStream basisStream = File.Open(FileMode.Open, FileAccess.Read, FileShare.Read))
                using (MemoryStream ms = new MemoryStream())
                {
                    SignatureWriter octoSigWriter = new SignatureWriter(ms);
                    SignatureBuilder signatureBuilder = new SignatureBuilder();
                    signatureBuilder.ProgressReporter = new OctoSigProgressReporter(File);
                    signatureBuilder.Build(basisStream, octoSigWriter);
                    op.Complete();
                    return OctoSignature = ms.ToArray();
                }
            }
        }
        #endregion

        #region Fields
        [NonSerialized]
        protected Logger<TransferEngine> L = new Logger<TransferEngine>();
        [NonSerialized]
        public FileInfo File;
        [NonSerialized]
        public CloudBlob FileBlob;
        public string FileName;
        public string FilePath;
        public string UserName;
        public string ComputerName;
        public string ETag;
        public long Size;
        public byte[] FileHash;
        public byte[] OctoSignature;
        #endregion
    }
}
