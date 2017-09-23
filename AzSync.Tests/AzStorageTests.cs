using System;
using Xunit;

namespace AzSync.Tests
{
    public class AzStorageTests
    {
        [Fact]
        public void CanParseConnectionString()   
        {
            string cs = AzStorage.GetConnectionString(new Uri(@"http://127.0.0.1:8440/testacc1"), @"1gy3lpE7Du1j5ljKiupgKzywSw2isjsdfdsfsdfsdsgfsgfdgfdgfd/YThisv/OVVLfIOv9kQ==");
        }
    }
}
