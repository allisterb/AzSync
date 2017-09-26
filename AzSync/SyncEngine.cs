using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

using Microsoft.Extensions.Configuration;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.DataMovement;
using Microsoft.WindowsAzure.Storage.RetryPolicies;

using Serilog;
using SerilogTimings;


namespace AzSync
{
    public class SyncEngine : ILogging
    {
        #region Enums
        public enum OperationType
        {
            COPY,
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
            
            foreach (PropertyInfo prop in this.GetType().GetProperties())
            {
                if (EngineOptions.ContainsKey(prop.Name) && prop.CanWrite)
                {
                    prop.SetValue(this, EngineOptions[prop.Name]);
                }
            }
            if (this.Operation == OperationType.COPY)
            {
                Contract.Requires(!string.IsNullOrEmpty(Source) && !string.IsNullOrEmpty(Destination));
                Contract.Requires(!(EngineOptions.ContainsKey("SourceUri") && EngineOptions.ContainsKey("DestinationUri")));
                if (this.EngineOptions.ContainsKey("DestinationUri"))
                {
                    Contract.Requires(EngineOptions.ContainsKey("DestinationKey") && EngineOptions.ContainsKey("DestinationAccountName") && EngineOptions.ContainsKey("DestinationContainerName"));
                    Contract.Requires(EngineOptions.ContainsKey("SourceFiles"));
                    string cs = UseStorageEmulator ? "UseDevelopmentStorage=true" : AzStorage.GetConnectionString(DestinationUri, DestinationKey);
                    if (string.IsNullOrEmpty(cs))
                    {
                        L.Error("Could not determine the Azure Storage connection string.");
                        return;
                    }
                    else
                    {
                        DestinationStorage = new AzStorage(this, cs, true);
                        if (!DestinationStorage.Initialised)
                        {
                            L.Error("Could not intialise destination Azure storage.");
                            Initialised = false;
                            return;
                        }
                        else
                        {
                            this.Initialised = true;
                        }
                    }
                }
                else
                {
                    Contract.Requires(EngineOptions.ContainsKey("SourceKey") && EngineOptions.ContainsKey("SourceAccountName") && EngineOptions.ContainsKey("SourceContainerName"));
                    Contract.Requires(EngineOptions.ContainsKey("DestinationDirectory"));
                    SourceUri = (Uri)EngineOptions["SourceUri"];
                    string cs = UseStorageEmulator ? "UseDevelopmentStorage=true" : AzStorage.GetConnectionString(SourceUri, SourceKey);
                    if (string.IsNullOrEmpty(cs))
                    {
                        L.Error("Could not determine the Azure Storage connection string.");
                        return;
                    }
                    else
                    {
                        SourceStorage = new AzStorage(this, cs, true);
                        if (!SourceStorage.Initialised)
                        {
                            L.Error("Could not intialise source Azure storage.");
                            Initialised = false;
                            return;
                        }
                        else
                        {
                            Initialised = true;
                        }
                    }
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
        public string[] SourceFiles { get; protected set; }
        public DirectoryInfo SourceDirectory { get; protected set; }
        public AzStorage SourceStorage { get; protected set; }
        public string Destination { get; protected set; }
        public Uri DestinationUri { get; protected set; }
        public string DestinationKey { get; protected set; }
        public string DestinationAccountName { get; protected set; }
        public string DestinationContainerName { get; protected set; }
        public DirectoryInfo DestinationDirectory { get; protected set; }
        public AzStorage DestinationStorage { get; protected set; }
        public string ContentType { get; protected set; }
        public int BlockSizeKB { get; protected set; }
        public int RetryCount { get; protected set; }
        public int RetryTime { get; protected set; }
        public bool Overwrite { get; protected set; }
        public string Pattern { get; protected set; }
        public bool Recurse { get; protected set; }
        public bool UseStorageEmulator { get; protected set; }
        #endregion

        #region Methods
        public async Task<bool> Sync()
        {
            switch (this.Operation)
            {
                case OperationType.COPY:
                    if (DestinationUri != null)
                    {
                        return await Upload();
                    }
                    else
                    {
                        return await Download();
                    }
                default:
                    throw new NotImplementedException();
            }
        }

        protected async Task<bool> Upload()
        {
            if (SourceFiles.Length == 1)
            {
                bool u = await UploadSingleFile(SourceFiles[0]);
                return u;
            }
            /*
            UploadDirectoryOptions options = new UploadDirectoryOptions
            {
                SearchPattern = Pattern,
                Recursive = Recurse,
                BlobType = BlobType.BlockBlob
            };
            DirectoryTransferContext ctx = new DirectoryTransferContext();
            ctx.LogLevel = LogLevel.Verbose;*/

            /*
            CloudBlob destinationBlob = await DestinationStorage.GetorCreateCloudBlobAsync(ContainerName, destinationBlobName, BlobType.BlockBlob);
            UploadOptions options = new UploadOptions();

            SingleTransferContext context = new SingleTransferContext();
            context.SetAttributesCallback = (destination) =>
            {
                Cloud=Blob destBlob = destination as CloudBlob;
                destBlob.Properties.ContentType = "image/png";
            };
            */

            return await Task.FromResult(true);
        }

        protected async Task<bool> UploadSingleFile(string fileName)
        {
            FileInfo file = new FileInfo(fileName);
            UploadOptions options = new UploadOptions();
            SingleTransferContext context = new SingleTransferContext();
            context.SetAttributesCallback = (destination) =>
            {
                CloudBlob destBlob = destination as CloudBlob;
                destBlob.Properties.ContentType = this.ContentType;
            };
            try
            {
                CloudBlob destinationBlob = await DestinationStorage.GetorCreateCloudBlobAsync(DestinationContainerName, file.Name, BlobType.BlockBlob);
                await TransferManager.UploadAsync(file.FullName, destinationBlob);
                return true;
            }
            catch (StorageException se)
            {
                L.Error(se, $"A storage error occurred attempting to upload file {file.Name} to a cloud block blob in container {DestinationContainerName}");
                return false;
            }
            catch (TransferException te)
            {
                L.Error(te, $"A transfer error occurred attempting to upload file {file.Name} to a cloud block blob in container {DestinationContainerName}");
                return false;
            }
        }

        protected async Task<bool> Download()
        {
            return await Task.FromResult(true);
        }
        #endregion
    }
}
