using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Octodiff.Diagnostics;

namespace AzSync
{
    public class OctoSigBuildProgressReporter : IProgressReporter, ILogging
    {
        #region Constructors
        public OctoSigBuildProgressReporter(FileInfo file)
        {
            File = file;
        }
        #endregion

        #region Methods
        public void ReportProgress(string operation, long currentPosition, long total)
        {
            markPosition = total / 10;
            if (operation.StartsWith("Hashing file") && currentPosition == 0)
            {
                L.Info("Hashing file {file}", File.FullName);
            }
            else if (operation.StartsWith("Building signatures") && (currentPosition >= mark * markPosition) && (currentPosition >= (mark + 1) * markPosition))
            {
                mark++;
                L.Info("Building signature for file {file} at current byte position {0}.", File.FullName, currentPosition);
            }
        }
        #endregion 

        #region Fields
        protected Logger<OctoSigBuildProgressReporter> L = new Logger<OctoSigBuildProgressReporter>();
        protected FileInfo File;
        int mark = 0;
        long markPosition;
        #endregion
    }
}
