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
    public abstract class Signature : ILogging
    {
        #region Constructors
        public Signature(DirectoryInfo source, string pattern, string[] sourceFiles, CloudBlob blob)
        {
            Source = source;
            Pattern = pattern;
            SourceFiles = sourceFiles;
            Blob = blob;
            ETag = blob.Properties.ETag;
        }
        #endregion

        #region Abstract methods
        public abstract byte[] Compute();
        #endregion

        #region Properties
        public bool Build { get; protected set; }
        #endregion


        #region Fields
        [NonSerialized]
        protected Logger<Signature> L = new Logger<Signature>();
        [NonSerialized]
        public CloudBlob Blob;
        [NonSerialized]
        public DirectoryInfo Source;
        public string Pattern;
        public string[] SourceFiles;
        public string UserName;
        public string ComputerName;
        public string ETag;
        public long Size;
        public byte[] Hash;
        public byte[] ComputedSignature;
        #endregion
    }
}
