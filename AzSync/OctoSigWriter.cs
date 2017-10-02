using System.IO;

using Octodiff.Core;

namespace AzSync
{
    class OctoSigWriter : ISignatureWriter
    {
        private readonly BinaryWriter signatureStream;

        public OctoSigWriter(Stream signatureStream)
        {
            this.signatureStream = new BinaryWriter(signatureStream);
        }

        public void WriteMetadata(IHashAlgorithm hashAlgorithm, IRollingChecksum rollingChecksumAlgorithm)
        {
            signatureStream.Write(BinaryFormats.OctoSig.SignatureHeader);
            signatureStream.Write(BinaryFormats.OctoSig.Version);
            signatureStream.Write(hashAlgorithm.Name);
            signatureStream.Write(rollingChecksumAlgorithm.Name);
            signatureStream.Write(BinaryFormats.OctoSig.EndOfMetadata);
        }

        public void WriteChunk(ChunkSignature signature)
        {
            signatureStream.Write(signature.Length);
            signatureStream.Write(signature.RollingChecksum);
            signatureStream.Write(signature.Hash);
        }
    }
}