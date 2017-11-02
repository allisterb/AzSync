using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Octodiff.Diagnostics;

namespace AzSync
{
    public class OctoSigReadProgressReporter : IProgressReporter, ILogging
    {
        #region Constructors
        public OctoSigReadProgressReporter(long streamLength)
        {
            this.streamLength = streamLength;
        }
        #endregion

        #region Methods
        public void ReportProgress(string operation, long currentPosition, long total)
        {
            markPosition = total / 10;
            if ((currentPosition >= mark * markPosition) && (currentPosition >= (mark + 1) * markPosition))
            {
                mark++;
                L.Info("Reading signature at current byte position {0}.", currentPosition);
            }
            
        }
        #endregion 

        #region Fields
        protected Logger<OctoSigReadProgressReporter> L = new Logger<OctoSigReadProgressReporter>();
        protected long streamLength;
        int mark = 0;
        long markPosition;
        #endregion
    }
}
