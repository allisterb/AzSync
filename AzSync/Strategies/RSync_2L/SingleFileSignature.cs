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
using Octodiff.Diagnostics;

namespace AzSync
{
    [Serializable]
    public class SingleFileSignature : Signature
    {
        #region Constructors
        public SingleFileSignature(FileInfo file, CloudBlob blob = null) : base(file.Directory, file.Name, new string[] { file.Name }, blob)
        {
            File = file;
            Size = File.Length;
        }

        public SingleFileSignature(Signature signature) : base(signature.Source, signature.Pattern, signature.SourceFiles, signature.Blob)
        {
            
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
                    signatureBuilder.ProgressReporter = new OctoSigBuildProgressReporter(File);
                    signatureBuilder.Build(basisStream, octoSigWriter);
                    ComputedDateTime = DateTime.UtcNow;
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
