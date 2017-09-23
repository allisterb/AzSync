using System;
using System.Collections.Generic;
using System.Text;

using CommandLine;
using CommandLine.Text;

namespace AzSync.CLI
{
    class Options
    {
        [Option('s', "source", HelpText = "The Azure Storage resource or local file-system object that will be the sync source. For an Azure Storage resource you must use your Blob Service endpoint Url.")]
        public string Source { get; set; }

        [Option('d', "dest", HelpText = "The Azure Storage resource or local file-system object that will be the sync destination. For an Azure Storage resource you must use your Blob Service endpoint Url.")]
        public string Destination { get; set; }

        /*
        [Option("source-account", HelpText = "The account name for the Azure Storage resource that will be the sync source.")]
        public string SourceAccount { get; set; }

        [Option("dest-account", HelpText = "The account name for the Azure Storage resource that will be the sync source.")]
        public string DestinationAccount { get; set; }
        */
        [Option("source-key", HelpText = "The account key for accessing the source Azure Storage resource.")]
        public string SourceKey { get; set; }

        [Option("dest-key", HelpText = "The account key for accessing the destinations Azure Storage resource.")]
        public string DestinationKey { get; set; }

        [Option("use-emulator", HelpText = "Use the Azure Storage emulator installed on the local machine.", Default = false)]
        public bool UseStorageEmulator { get; set; }


        [Option("block-size", HelpText = "The Azure Storage blob block size in kilobytes. Default is 4096.", Default = 4096)]
        public int BlockSizeKB { get; set; }
    }

    [Verb("up", HelpText = "Upload local file-system objects to Azure Storage without synchronization.")]
    class UpOptions : Options
    {

    }

    [Verb("down", HelpText = "Download Azure Storage objects to the local file-system without synchronization.")]
    class DownOptions : Options
    {

    }

    [Verb("sync", HelpText = "Synchronize local file-system objects with Azure Storage objects.")]
    class SyncOptions : Options
    {

    }
}
