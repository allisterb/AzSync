using System.Text;

namespace AzSync.BinaryFormats
{
    public class SyncJournal
    {
        public const byte Version = 0x01;
        public static readonly byte[] JournalHeader = Encoding.ASCII.GetBytes("AZSYNC");
        public static readonly byte[] EndOfMetadata = Encoding.ASCII.GetBytes(">>>");
    }
}