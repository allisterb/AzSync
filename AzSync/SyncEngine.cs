using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Microsoft.Extensions.Configuration;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.DataMovement;

using Serilog;
using SerilogTimings;


namespace AzSync
{
    public class SyncEngine : ILogging
    {
        #region Enums
        public enum OperationType
        {
            UPLOAD,
            DOWNLOAD,
            SYNC,
            INFO
        }
        #endregion

        #region Constructors
        public SyncEngine(Dictionary<string, object> engineOptions, IConfigurationRoot configuration, TextWriter output)
        {
            Contract.Requires(engineOptions.ContainsKey("OperationType"));
            Contract.Requires(engineOptions.ContainsKey("Source") || engineOptions.ContainsKey("Destination"));
            EngineOptions = engineOptions;
            AppConfig = configuration;
            Operation = (OperationType)EngineOptions["OperationType"];
            UseStorageEmulator = (bool)EngineOptions["UseStorageEmulator"];
            if (EngineOptions.ContainsKey("Source"))
            {
                Source = (string)EngineOptions["Source"];
            }
            if (EngineOptions.ContainsKey("SourceKey"))
            {
                SourceKey = (string)EngineOptions["SourceKey"];
            }
            if (EngineOptions.ContainsKey("SourceAccountName"))
            {
                SourceAccountName = (string)EngineOptions["SourceAccountName"];
            }
            if (EngineOptions.ContainsKey("SourceContainerName"))
            {
                SourceContainerName = (string)EngineOptions["SourceContainerName"];
            }
            if (EngineOptions.ContainsKey("Destination"))
            {
                Destination = (string)EngineOptions["Destination"];
            }
            if (EngineOptions.ContainsKey("DestinationKey"))
            {
                DestinationKey = (string)EngineOptions["DestinationKey"];
            }
            if (EngineOptions.ContainsKey("DestinationAccountName"))
            {
                DestinationAccountName = (string)EngineOptions["DestinationAccountName"];
            }
            if (EngineOptions.ContainsKey("DestinationContainerName"))
            {
                DestinationContainerName = (string)EngineOptions["DestinationContainerName"];
            }
            if (EngineOptions.ContainsKey("SourceFile"))
            {
                SourceFile = (FileInfo)EngineOptions["SourceFile"];
            }
            if (EngineOptions.ContainsKey("SourceDirectory"))
            {
                SourceDirectory = (DirectoryInfo)EngineOptions["SourceDirectory"];
            }
            if (this.Operation == OperationType.UPLOAD)
            {
                Contract.Requires(Operation == OperationType.UPLOAD && !string.IsNullOrEmpty(Source) && !string.IsNullOrEmpty(Destination) 
                    && EngineOptions.ContainsKey("DestinationUri"));
                Contract.Requires(EngineOptions.ContainsKey("DestinationKey") && EngineOptions.ContainsKey("DestinationAccountName") && EngineOptions.ContainsKey("DestinationContainerName"));
                DestinationUri = (Uri)EngineOptions["DestinationUri"];
                string cs = UseStorageEmulator ? "UseDevelopmentStorage=true" : AzStorage.GetConnectionString(DestinationUri, DestinationKey);
                if (string.IsNullOrEmpty(cs))
                {
                    L.Error("Could not determine the Azure Storage connection string.");
                    return;
                }
                else
                {
                    DestinationStorage = new AzStorage(cs);
                }
                if (DestinationStorage.Initialised)
                {
                    Initialised = true;
                }
                else
                {
                    Initialised = false;
                }

            }
            
        }
        #endregion

        #region Properties
        protected Logger<SyncEngine> L = new Logger<SyncEngine>();
        public bool Initialised { get; protected set; } = false;
        public IConfigurationRoot AppConfig { get; protected set; }
        public Dictionary<string, object> EngineOptions { get; protected set; }
        public OperationType Operation { get; protected set; }
        public string Source { get; protected set; }
        public Uri SourceUri { get; protected set; }
        public string SourceKey { get; protected set; }
        public string SourceAccountName { get; protected set; }
        public string SourceContainerName { get; protected set; }
        public FileInfo SourceFile { get; protected set; }
        public DirectoryInfo SourceDirectory { get; protected set; }
        public string Destination { get; protected set; }
        public Uri DestinationUri { get; protected set; }
        public string DestinationKey { get; protected set; }
        public string DestinationAccountName { get; protected set; }
        public string DestinationContainerName { get; protected set; }
        public FileInfo DestinationFile { get; protected set; }
        public FileInfo DestinationDirectory { get; protected set; }
        #endregion

        #region Methods
        public async Task<bool> Sync()
        {
            switch (this.Operation)
            {
                case OperationType.UPLOAD:    
                    return await Upload();
                default:
                    throw new NotImplementedException();
            }
        }

        protected async Task<bool> Upload()
        {
            /*
            CloudBlob destinationBlob = await DestinationStorage.GetorCreateCloudBlobAsync(ContainerName, destinationBlobName, BlobType.BlockBlob);
            UploadOptions options = new UploadOptions();

            SingleTransferContext context = new SingleTransferContext();
            context.SetAttributesCallback = (destination) =>
            {
                CloudBlob destBlob = destination as CloudBlob;
                destBlob.Properties.ContentType = "image/png";
            };
            */
            return await Task.FromResult(true);
        }
        #endregion

        #region Properties
        bool UseStorageEmulator { get; set; }
        AzStorage SourceStorage { get; set; }
        AzStorage DestinationStorage { get; set; }
        string ContainerName { get; set; }
        #endregion
    }
}
