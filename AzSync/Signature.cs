using System;
using System.Collections.Generic;
using System.Text;

namespace AzSync
{
    public class FileSignature
    {

        public string UserName { get; set; }
        public string ComputerName { get; set; }
        public byte[] OctoSig { get; set; }
    }
}
