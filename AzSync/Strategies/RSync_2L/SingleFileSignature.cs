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
    public class SingleFileSignature : Signature
    {
        #region Constructors
        public SingleFileSignature(FileInfo file, CloudBlob blob) : base(file.Directory, file.Name, new string[] { file.Name }, blob)
        {
            File = file;
            Size = File.Length;
        }
        #endregion

        #region Overriden Methods
        public override byte[] Compute()
        {
            using (Operation op = L.Begin("Build file signature for file {file}", File.FullName))
            {
                using (FileStream basisStream = File.Open(FileMode.Open, FileAccess.Read, FileShare.Read))
                using (MemoryStream ms = new MemoryStream())
                {
                    SignatureWriter octoSigWriter = new SignatureWriter(ms);
                    SignatureBuilder signatureBuilder = new SignatureBuilder();
                    signatureBuilder.ProgressReporter = new OctoSigProgressReporter(File);
                    signatureBuilder.Build(basisStream, octoSigWriter);
                    op.Complete();
                    return ComputedSignature = ms.ToArray();
                }
            }
        }
        #endregion

        #region Fields
        [NonSerialized]
        public FileInfo File;
        #endregion
    }
}
