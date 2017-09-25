using System;
using System.Collections.Generic;
using System.Text;

using CommandLine;
using CommandLine.Text;

namespace AzSync.CLI
{
    class Options
    {
        [Option('s', "source", HelpText = "The Azure Storage resource or local directory that will be the sync source. For an Azure Storage resource you must use your Blob Service endpoint Url.")]
        public string Source { get; set; }

        [Option('d', "dest", HelpText = "The Azure Storage resource or local filesystem object that will be the sync destination. Use a single file or direcctory name if specifying a local sync destination. For an Azure Storage resource you must use your Blob Service endpoint Url.")]
        public string Destination { get; set; }

        [Option('p', "pattern", HelpText = "The pattern to match names against in the sync source or destination. Use the standard OS wildcards for local file or directory names if specifying a local sync source.", Default = "*")]
        public string Pattern { get; set; }

        [Option('R', "recurse", HelpText = "Recurse into lower level sub-directories when searching for local file or directory names.", Default = false)]
        public bool Recurse { get; set; }

        [Option('r', "retries", HelpText = "The number of times to retry an Azure Storage operation which does not complete successfully.", Default = 3)]
        public int Retries { get; set; }
        
        [Option("source-key", HelpText = "The account key for accessing the source Azure Storage resource.")]
        public string SourceKey { get; set; }

        [Option("dest-key", HelpText = "The account key for accessing the destinations Azure Storage resource.")]
        public string DestinationKey { get; set; }

        [Option("use-emulator", HelpText = "Use the Azure Storage emulator installed on the local machine.", Default = false)]
        public bool UseStorageEmulator { get; set; }

        [Option("block-size", HelpText = "The Azure Storage blob block size in kilobytes. Default is 4096.", Default = 4096)]
        public int BlockSizeKB { get; set; }
    }

    [Verb("copy", HelpText = "Copy files and folders between the local filesystem and Azure Storage without synchronization.")]
    class CopyOptions : Options
    {

    }

    [Verb("sync", HelpText = "Synchronize files and folders between the local filesystem and Azure Storage.")]
    class SyncOptions : Options
    {

    }
}
