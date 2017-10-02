using System.Text;

namespace AzSync.BinaryFormats
{
    public class OctoSig
    {
        public const byte Version = 0x01;
        public static readonly byte[] SignatureHeader = Encoding.ASCII.GetBytes("OCTOSIG");
        public static readonly byte[] DeltaHeader = Encoding.ASCII.GetBytes("OCTODELTA");
        public static readonly byte[] EndOfMetadata = Encoding.ASCII.GetBytes(">>>");
        public const byte CopyCommand = 0x60;
        public const byte DataCommand = 0x80;
    }

    public class SyncJournal
    {
        public const byte Version = 0x01;
        public static readonly byte[] JournalHeader = Encoding.ASCII.GetBytes("AZSYNC");
        public static readonly byte[] EndOfMetadata = Encoding.ASCII.GetBytes(">>>");
    }
}